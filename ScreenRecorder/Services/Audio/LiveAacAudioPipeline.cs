using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly bool _micEnabled;
    private readonly bool _sysEnabled;
    private readonly string? _micDeviceId;

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
        if (!_micEnabled && !_sysEnabled)
            return;

        _muxer.ConfigureAudio(TargetSampleRate, TargetChannels);

        if (_micEnabled)
            TryStartMic();

        if (_sysEnabled)
            TryStartSystem();

        _stopRequested = false;
        _workerThread = new Thread(AudioEncodeWorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Forecord-LiveAacAudio"
        };
        _workerThread.Start();
    }

    public void StopAndFlush()
    {
        _stopRequested = true;
        try { _micCapture?.StopRecording(); } catch { }
        try { _sysCapture?.StopRecording(); } catch { }
        try { _silenceOut?.Stop(); } catch { }

        _workerThread?.Join(3000);
        _workerThread = null;

        DisposeCaptures();
    }

    private void TryStartMic()
    {
        try
        {
            var device = ResolveMicrophoneDevice(_micDeviceId);
            if (device == null) return;

            _micCapture = new WasapiCapture(device);
            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _micResampler = BuildNormalizedSampleProvider(_micBuffer);
            _micCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                    _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _micCapture.StartRecording();
        }
        catch (Exception ex)
        {
            RecordingDiagnostics.ReportError(
                DiagnosticStage.AudioPipe,
                "Failed to initialize WASAPI microphone for live AAC pipeline.",
                ex);
        }
    }

    private void TryStartSystem()
    {
        try
        {
            _sysCapture = new WasapiLoopbackCapture();
            StartSilenceKeeper(_sysCapture.WaveFormat);
            _sysBuffer = new BufferedWaveProvider(_sysCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _sysResampler = BuildNormalizedSampleProvider(_sysBuffer);
            _sysCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                    _sysBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _sysCapture.StartRecording();
        }
        catch (Exception ex)
        {
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

                    encoder.ProcessPcmChunk(pcmChunk, frameTime100Ns: (generatedSamplesPerChannel - AacFrameSamplesPerChannel) * 10_000_000L / TargetSampleRate, aacPacket =>
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

                encoder.ProcessPcmChunk(pcmChunk, frameTime100Ns: (generatedSamplesPerChannel - AacFrameSamplesPerChannel) * 10_000_000L / TargetSampleRate, aacPacket =>
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

        // Drift compensation: if a capture ring buffer accumulated more than
        // 180 ms of audio (e.g. device clock runs slightly faster than QPC),
        // drop the excess so latency/drift never grows over a 15-min recording.
        TrimExcessBuffer(_micBuffer);
        TrimExcessBuffer(_sysBuffer);

        var hasMic = _micResampler != null && _micBuffer != null && _micBuffer.BufferedBytes > 0;
        var hasSys = _sysResampler != null && _sysBuffer != null && _sysBuffer.BufferedBytes > 0;

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

        for (var i = 0; i < count; i++)
        {
            var sample16 = (short)Math.Clamp(Math.Round(scratchA[i] * 32767f), short.MinValue, short.MaxValue);
            pcmOut[i * 2] = (byte)(sample16 & 0xFF);
            pcmOut[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }
    }

    private static void TrimExcessBuffer(BufferedWaveProvider? buffer)
    {
        if (buffer == null) return;
        var maxBytes = buffer.WaveFormat.AverageBytesPerSecond / 5; // 200 ms ceiling
        if (buffer.BufferedBytes > maxBytes)
            buffer.ClearBuffer();
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
        var enumerator = new MMDeviceEnumerator();
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

    private void DisposeCaptures()
    {
        try { _silenceOut?.Dispose(); } catch { }
        _silenceOut = null;

        try { _micCapture?.Dispose(); } catch { }
        _micCapture = null;

        try { _sysCapture?.Dispose(); } catch { }
        _sysCapture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndFlush();
    }
}
