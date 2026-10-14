using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenRecorder.Services.Diagnostics;
using ScreenRecorder.Services.Muxing;

namespace ScreenRecorder.Services.Audio;

/// <summary>
/// Captures microphone and/or system loopback audio via WASAPI, locks the
/// audio timeline to the shared video <see cref="Stopwatch"/> clock to prevent
/// A/V drift across long recordings, mixes to 48 kHz 16-bit Stereo PCM, and
/// encodes 1024-sample AAC-LC frames in real time via the built-in Windows
/// Media Foundation AAC encoder MFT directly into <see cref="H264Mp4Muxer"/>.
/// Requires zero FFmpeg processes.
/// </summary>
internal sealed class LiveAacAudioPipeline : IDisposable
{
    public const int TargetSampleRate = 48000;
    public const int TargetChannels = 2;
    public const int AacFrameSamplesPerChannel = 1024;
    private const int BytesPerSample = 2; // 16-bit PCM
    private const int BytesPerFrame = TargetChannels * BytesPerSample; // 4 bytes per stereo frame
    private const int PcmChunkBytes = AacFrameSamplesPerChannel * BytesPerFrame; // 4096 bytes per AAC frame

    private readonly H264Mp4Muxer _muxer;
    private readonly Stopwatch _clock;
    private volatile bool _micEnabled;
    private volatile bool _sysEnabled;
    private string? _micDeviceId;
    private readonly object _deviceLock = new();

    private MMDevice? _micDevice;
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _sysCapture;
    private WasapiOut? _silenceOut;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _sysBuffer;
    private ISampleProvider? _micResampler;
    private ISampleProvider? _sysResampler;

    private Thread? _workerThread;
    private volatile bool _stopRequested;
    private long _emittedAacFrames;
    private bool _disposed;

    public long EmittedAacFrames => Interlocked.Read(ref _emittedAacFrames);

    public LiveAacAudioPipeline(
        H264Mp4Muxer muxer,
        Stopwatch sharedVideoClock,
        bool micEnabled,
        bool sysEnabled,
        string? micDeviceId)
    {
        _muxer = muxer ?? throw new ArgumentNullException(nameof(muxer));
        _clock = sharedVideoClock ?? throw new ArgumentNullException(nameof(sharedVideoClock));
        _micEnabled = micEnabled;
        _sysEnabled = sysEnabled;
        _micDeviceId = micDeviceId;
    }

    public void Start()
    {
        _muxer.ConfigureAudio(TargetSampleRate, TargetChannels);

        lock (_deviceLock)
        {
            if (_micEnabled)
                TryStartMic();

            if (_sysEnabled)
                TryStartSystem();
        }

        _stopRequested = false;
        _workerThread = new Thread(AudioEncodeWorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Forecord-LiveAacAudio"
        };
        _workerThread.Start();
    }

    public void UpdateAudioSources(bool micEnabled, bool sysEnabled, string? micDeviceId)
    {
        lock (_deviceLock)
        {
            if (_disposed || _stopRequested)
                return;

            var micDeviceChanged = !string.Equals(_micDeviceId, micDeviceId, StringComparison.Ordinal);
            _micDeviceId = micDeviceId;

            if ((!micEnabled || micDeviceChanged) && _micCapture != null)
            {
                StopAndDisposeMic();
            }

            if (micEnabled && _micCapture == null)
            {
                TryStartMic();
            }

            if (!sysEnabled && _sysCapture != null)
            {
                StopAndDisposeSys();
            }
            else if (sysEnabled && _sysCapture == null)
            {
                TryStartSystem();
            }

            _micEnabled = micEnabled;
            _sysEnabled = sysEnabled;
        }
    }

    public void StopAndFlush()
    {
        _stopRequested = true;
        lock (_deviceLock)
        {
            try { _micCapture?.StopRecording(); } catch { }
            try { _sysCapture?.StopRecording(); } catch { }
            try { _silenceOut?.Stop(); } catch { }
        }

        _workerThread?.Join(3000);
        _workerThread = null;

        lock (_deviceLock)
        {
            DisposeCaptures();
        }
    }

    private void TryStartMic()
    {
        StopAndDisposeMic();
        var device = ResolveMicrophoneDevice(_micDeviceId);
        if (device == null) return;

        _micDevice = device;
        if (!TryStartMicWithFormat(device, null))
        {
            if (!TryStartMicWithFormat(device, new WaveFormat(TargetSampleRate, 16, TargetChannels)))
            {
                StopAndDisposeMic();
            }
        }
    }

    private bool TryStartMicWithFormat(MMDevice device, WaveFormat? overrideFormat)
    {
        try
        {
            var cap = overrideFormat == null
                ? new WasapiCapture(device)
                : new WasapiCapture(device, true, 60) { WaveFormat = overrideFormat };
            var buf = new BufferedWaveProvider(cap.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            var resampler = BuildNormalizedSampleProvider(buf);
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                    buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _micCapture = cap;
            _micBuffer = buf;
            _micResampler = resampler;
            cap.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            try { _micCapture?.Dispose(); } catch { }
            _micCapture = null;
            _micBuffer = null;
            _micResampler = null;
            if (overrideFormat != null)
            {
                RecordingDiagnostics.ReportError(
                    DiagnosticStage.AudioPipe,
                    "Failed to initialize WASAPI microphone for live AAC pipeline.",
                    ex);
            }
            return false;
        }
    }

    private void TryStartSystem()
    {
        StopAndDisposeSys();
        try
        {
            var cap = new WasapiLoopbackCapture();
            StartSilenceKeeper(cap.WaveFormat);
            var buf = new BufferedWaveProvider(cap.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            var resampler = BuildNormalizedSampleProvider(buf);
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                    buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _sysCapture = cap;
            _sysBuffer = buf;
            _sysResampler = resampler;
            cap.StartRecording();
        }
        catch (Exception ex)
        {
            StopAndDisposeSys();
            RecordingDiagnostics.ReportError(
                DiagnosticStage.AudioPipe,
                "Failed to initialize WASAPI loopback for live AAC pipeline.",
                ex);
        }
    }

    private static ISampleProvider BuildNormalizedSampleProvider(IWaveProvider source)
    {
        var sampleProvider = source.ToSampleProvider();
        if (sampleProvider.WaveFormat.Channels == 1)
            sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
        else if (sampleProvider.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sampleProvider }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sampleProvider = mux;
        }

        if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

        return sampleProvider;
    }

    private void AudioEncodeWorkerLoop()
    {
        MediaFoundationApi.Startup();
        try
        {
            using var encoder = new MfAacFrameEncoder(TargetSampleRate, TargetChannels, bitrate: 192000);
            var floatScratchA = new float[AacFrameSamplesPerChannel * TargetChannels];
            var floatScratchB = new float[AacFrameSamplesPerChannel * TargetChannels];
            var pcmChunk = new byte[PcmChunkBytes];
            long generatedSamplesPerChannel = 0;

            while (!_stopRequested)
            {
                var elapsedSeconds = _clock.Elapsed.TotalSeconds;
                var targetSamplesPerChannel = (long)(elapsedSeconds * TargetSampleRate);
                var availableChunks = (targetSamplesPerChannel - generatedSamplesPerChannel) / AacFrameSamplesPerChannel;

                if (availableChunks <= 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                // Cap burst catch-up to prevent huge spikes if the thread woke up late
                var chunksToProcess = (int)Math.Min(availableChunks, 8);
                for (var c = 0; c < chunksToProcess; c++)
                {
                    MixNext1024StereoSamples(floatScratchA, floatScratchB, pcmChunk);
                    generatedSamplesPerChannel += AacFrameSamplesPerChannel;

                    encoder.ProcessPcmChunk(
                        pcmChunk,
                        frameTime100Ns: (generatedSamplesPerChannel - AacFrameSamplesPerChannel) * 10_000_000L / TargetSampleRate,
                        aacPacket =>
                        {
                            _muxer.AddAudioPacket(aacPacket, AacFrameSamplesPerChannel);
                            Interlocked.Increment(ref _emittedAacFrames);
                        });
                }
            }

            // Final flush up to exact stop timestamp
            var finalSamplesPerChannel = (long)(_clock.Elapsed.TotalSeconds * TargetSampleRate);
            while (generatedSamplesPerChannel + AacFrameSamplesPerChannel <= finalSamplesPerChannel)
            {
                MixNext1024StereoSamples(floatScratchA, floatScratchB, pcmChunk);
                generatedSamplesPerChannel += AacFrameSamplesPerChannel;

                encoder.ProcessPcmChunk(
                    pcmChunk,
                    frameTime100Ns: (generatedSamplesPerChannel - AacFrameSamplesPerChannel) * 10_000_000L / TargetSampleRate,
                    aacPacket =>
                    {
                        _muxer.AddAudioPacket(aacPacket, AacFrameSamplesPerChannel);
                        Interlocked.Increment(ref _emittedAacFrames);
                    });
            }

            encoder.Drain(aacPacket =>
            {
                _muxer.AddAudioPacket(aacPacket, AacFrameSamplesPerChannel);
                Interlocked.Increment(ref _emittedAacFrames);
            });
        }
        catch (Exception ex)
        {
            RecordingDiagnostics.ReportError(
                DiagnosticStage.AudioPipe,
                "LiveAacAudioPipeline worker encountered an error.",
                ex);
        }
        finally
        {
            try { MediaFoundationApi.Shutdown(); } catch { }
        }
    }

    private void MixNext1024StereoSamples(float[] scratchA, float[] scratchB, byte[] pcmOut)
    {
        var count = AacFrameSamplesPerChannel * TargetChannels;
        Array.Clear(scratchA, 0, count);

        lock (_deviceLock)
        {
            TrimExcessBuffer(_micBuffer);
            TrimExcessBuffer(_sysBuffer);

            var hasMic = _micEnabled && _micResampler != null && HasEnoughBufferedForOneAacFrame(_micBuffer);
            var hasSys = _sysEnabled && _sysResampler != null && HasEnoughBufferedForOneAacFrame(_sysBuffer);

            if (hasMic)
            {
                _micResampler!.Read(scratchA, 0, count);
            }

            if (hasSys)
            {
                Array.Clear(scratchB, 0, count);
                _sysResampler!.Read(scratchB, 0, count);
                for (var i = 0; i < count; i++)
                    scratchA[i] = Math.Clamp(scratchA[i] + scratchB[i], -1.0f, 1.0f);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var sample16 = (short)Math.Clamp(Math.Round(scratchA[i] * 32767f), short.MinValue, short.MaxValue);
            pcmOut[i * 2] = (byte)(sample16 & 0xFF);
            pcmOut[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }
    }

    private static bool HasEnoughBufferedForOneAacFrame(BufferedWaveProvider? buffer)
    {
        if (buffer == null) return false;
        // Require at least ~22 ms of audio (~1024 samples at 48kHz) so ReadFully
        // doesn't pad partial WASAPI packets with trailing zeros mid-sentence.
        var minBytes = Math.Max(4, buffer.WaveFormat.AverageBytesPerSecond * AacFrameSamplesPerChannel / TargetSampleRate);
        return buffer.BufferedBytes >= minBytes;
    }

    private static void TrimExcessBuffer(BufferedWaveProvider? buffer)
    {
        if (buffer == null) return;
        var avgBytesPerSec = Math.Max(1, buffer.WaveFormat.AverageBytesPerSecond);
        var maxBytes = avgBytesPerSec / 5; // 200 ms ceiling
        var buffered = buffer.BufferedBytes;
        if (buffered <= maxBytes) return;

        var targetBytes = avgBytesPerSec / 10; // keep ~100 ms of audio intact
        var blockAlign = Math.Max(1, buffer.WaveFormat.BlockAlign);
        var dropBytes = ((buffered - targetBytes) / blockAlign) * blockAlign;
        if (dropBytes <= 0) return;

        var discard = new byte[Math.Min(dropBytes, 16384)];
        var remaining = dropBytes;
        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, discard.Length);
            var read = buffer.Read(discard, 0, toRead);
            if (read <= 0) break;
            remaining -= read;
        }
    }

    private void StartSilenceKeeper(WaveFormat format)
    {
        try
        {
            if (_silenceOut != null) return;
            var silence = new SilenceProvider(format);
            var outDevice = new WasapiOut(AudioClientShareMode.Shared, 50);
            outDevice.Init(silence);
            outDevice.Play();
            _silenceOut = outDevice;
        }
        catch
        {
        }
    }

    private static MMDevice? ResolveMicrophoneDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                try { return enumerator.GetDevice(deviceId); } catch { }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch
        {
            return null;
        }
    }

    private void StopAndDisposeMic()
    {
        try { _micCapture?.StopRecording(); } catch { }
        try { _micCapture?.Dispose(); } catch { }
        _micCapture = null;
        try { _micDevice?.Dispose(); } catch { }
        _micDevice = null;
        _micBuffer = null;
        _micResampler = null;
    }

    private void StopAndDisposeSys()
    {
        try { _sysCapture?.StopRecording(); } catch { }
        try { _sysCapture?.Dispose(); } catch { }
        _sysCapture = null;
        _sysBuffer = null;
        _sysResampler = null;
        try { _silenceOut?.Stop(); } catch { }
        try { _silenceOut?.Dispose(); } catch { }
        _silenceOut = null;
    }

    private void DisposeCaptures()
    {
        StopAndDisposeMic();
        StopAndDisposeSys();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndFlush();
    }
}
