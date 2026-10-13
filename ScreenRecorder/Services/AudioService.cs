using System.IO;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Models;

namespace ScreenRecorder.Services;

/// <summary>
/// Manages microphone and system-audio capture for recording segments.
/// </summary>
public class AudioService : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _sysCapture;
    private WasapiOut? _silenceOut;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _sysWriter;
    private NamedPipeServerStream? _micPipe;
    private NamedPipeServerStream? _sysPipe;
    private readonly object _micLock = new();
    private readonly object _sysLock = new();

    public string? MicFile { get; private set; }
    public string? SystemFile { get; private set; }

    public List<Models.AudioDevice> GetMicrophoneDevices()
    {
        var result = new List<Models.AudioDevice>();
        using var e = new MMDeviceEnumerator();
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            result.Add(new Models.AudioDevice { Id = d.ID, Name = d.FriendlyName });
        return result;
    }

    public void Start(string segmentDir, bool mic, bool system, string? micId)
    {
        MicFile = null;
        SystemFile = null;

        if (mic) StartMic(segmentDir, micId);
        if (system) StartSystem(segmentDir);
    }

    public List<LiveAudioPipeInput> PrepareLivePipes(bool mic, bool system, string? micId)
    {
        Stop();
        MicFile = null;
        SystemFile = null;

        var inputs = new List<LiveAudioPipeInput>();
        if (mic)
        {
            var input = PrepareLiveMic(micId);
            if (input != null) inputs.Add(input);
        }
        if (system)
        {
            var input = PrepareLiveSystem();
            if (input != null) inputs.Add(input);
        }

        return inputs;
    }

    public async Task ConnectLivePipesAsync()
    {
        var tasks = new List<Task>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (_micPipe != null)
        {
            var pipe = _micPipe;
            var cap = _micCapture;
            tasks.Add(Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                if (cap != null)
                {
                    var prime = new byte[Math.Max(4, (cap.WaveFormat.AverageBytesPerSecond / 100) & ~7)];
                    lock (_micLock) WritePipe(pipe, prime, prime.Length);
                    try { cap.StartRecording(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Audio] mic StartRecording: {ex.Message}"); }
                }
            }, timeout.Token));
        }
        if (_sysPipe != null)
        {
            var pipe = _sysPipe;
            var cap = _sysCapture;
            tasks.Add(Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                if (cap != null)
                {
                    StartSilenceKeeper(cap.WaveFormat);
                    var prime = new byte[Math.Max(4, (cap.WaveFormat.AverageBytesPerSecond / 100) & ~7)];
                    lock (_sysLock) WritePipe(pipe, prime, prime.Length);
                    try { cap.StartRecording(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Audio] sys StartRecording: {ex.Message}"); }
                }
            }, timeout.Token));
        }
        try
        {
            if (tasks.Count > 0) await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("FFmpeg ses kanallarina baglanamadi.", ex);
        }
    }

    public void StartLivePipes()
    {
        // Captures are already started immediately upon pipe connection in ConnectLivePipesAsync
        // to avoid FFmpeg sequential input-probe deadlock.
    }

    private LiveAudioPipeInput? PrepareLiveMic(string? deviceId)
    {
        WasapiCapture? capture = null;
        NamedPipeServerStream? pipe = null;
        try
        {
            var device = ResolveMicrophoneDevice(deviceId);
            if (device == null) return null;

            capture = new WasapiCapture(device);
            var pipeName = $"Forecord_mic_{Guid.NewGuid():N}";
            pipe = CreateAudioPipe(pipeName);
            capture.DataAvailable += (_, e) =>
            {
                lock (_micLock) WritePipe(_micPipe, e.Buffer, e.BytesRecorded);
            };

            _micCapture = capture;
            _micPipe = pipe;
            return ToLiveInput(pipeName, capture.WaveFormat);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Live mic prepare failed: {ex.Message}");
            try { pipe?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
            return null;
        }
    }

    private LiveAudioPipeInput? PrepareLiveSystem()
    {
        WasapiLoopbackCapture? capture = null;
        NamedPipeServerStream? pipe = null;
        try
        {
            capture = new WasapiLoopbackCapture();
            var pipeName = $"Forecord_sys_{Guid.NewGuid():N}";
            pipe = CreateAudioPipe(pipeName);
            capture.DataAvailable += (_, e) =>
            {
                lock (_sysLock) WritePipe(_sysPipe, e.Buffer, e.BytesRecorded);
            };

            _sysCapture = capture;
            _sysPipe = pipe;
            return ToLiveInput(pipeName, capture.WaveFormat);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Live system prepare failed: {ex.Message}");
            try { pipe?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
            return null;
        }
    }

    // Out buffer = 1 MB. WASAPI f32 stereo @48kHz produces ~384 KB/s, so this is
    // ~2.7 seconds of headroom. The 5-arg constructor leaves outBufferSize at
    // the system default (~4 KB) which is barely 10 ms — any momentary ffmpeg
    // reader stall makes Write() block on the WASAPI callback thread, and the
    // next WASAPI buffer overruns. Result heard in playback as cut/choppy audio.
    private const int AudioPipeOutBuffer = 1 << 20;

    private static NamedPipeServerStream CreateAudioPipe(string pipeName) =>
        new(pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: AudioPipeOutBuffer);

    private static LiveAudioPipeInput ToLiveInput(string pipeName, WaveFormat format) =>
        new(
            $@"\\.\pipe\{pipeName}",
            format.SampleRate,
            format.Channels,
            Math.Max(format.BitsPerSample, 16),
            IsFloatFormat(format));

    private static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

    private static void WritePipe(NamedPipeServerStream? pipe, byte[] buffer, int bytes)
    {
        if (pipe?.IsConnected != true) return;
        try { pipe.Write(buffer, 0, bytes); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Silence keeper hatasi: {ex.Message}");
        }
    }

    private void StartMic(string dir, string? deviceId)
    {
        try
        {
            var device = ResolveMicrophoneDevice(deviceId);
            if (device == null) return;

            _micCapture = new WasapiCapture(device);
            MicFile = Path.Combine(dir, "mic.wav");
            _micWriter = new WaveFileWriter(MicFile, _micCapture.WaveFormat);
            _micCapture.DataAvailable += (_, e) =>
            {
                lock (_micLock) _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };
            _micCapture.StartRecording();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Mikrofon hatasi: {ex.Message}");
        }
    }

    private void StartSystem(string dir)
    {
        try
        {
            _sysCapture = new WasapiLoopbackCapture();
            StartSilenceKeeper(_sysCapture.WaveFormat);
            SystemFile = Path.Combine(dir, "system.wav");
            _sysWriter = new WaveFileWriter(SystemFile, _sysCapture.WaveFormat);
            _sysCapture.DataAvailable += (_, e) =>
            {
                lock (_sysLock) _sysWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };
            _sysCapture.StartRecording();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Sistem sesi hatasi: {ex.Message}");
        }
    }

    private static MMDevice? ResolveMicrophoneDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                try { device = enumerator.GetDevice(deviceId); } catch { device = null; }
            }

            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Mikrofon cihazi bulunamadi: {ex.Message}");
            return null;
        }

        return device;
    }

    // Two-phase stop:
    //   1. StopCaptureFlow — tell NAudio to stop; no new DataAvailable events
    //      fire AFTER the current in-flight callback returns.
    //   2. DisposeCaptures — under each lock so we can never race a callback
    //      that is mid-Write against pipe/writer disposal.
    // Live-mux callers may call StopCaptureFlow() before closing ffmpeg's
    // video stdin, then DisposeCaptures() afterwards (so ffmpeg sees audio EOF
    // only once it has finished processing in-flight buffered samples).
    public void Stop()
    {
        StopCaptureFlow();
        DisposeCaptures();
    }

    public void StopCaptureFlow()
    {
        // NAudio's StopRecording sets a flag; the audio thread keeps draining
        // its ring buffer for a few ms after this returns. We don't sleep here
        // — the lock inside DisposeCaptures synchronises with any in-flight
        // DataAvailable callback so the writer/pipe is never torn down while
        // a callback is mid-Write.
        try { _micCapture?.StopRecording(); } catch { }
        try { _sysCapture?.StopRecording(); } catch { }
        try { _silenceOut?.Stop(); } catch { }
    }

    // Blocks until ffmpeg has consumed all bytes currently buffered in the
    // audio named pipes, up to timeoutMs. CRITICAL for live-mux: without this,
    // Dispose() yanks the pipe before ffmpeg's reader drains the kernel
    // buffer (up to 1 MB now), so the tail of the AAC stream is truncated and
    // the muxer writes a malformed last frame → player errors mid-playback.
    public void DrainLivePipes(int timeoutMs = 2500)
    {
        var pipes = new List<NamedPipeServerStream>();
        var mp = _micPipe;
        var sp = _sysPipe;
        if (mp != null && mp.IsConnected) pipes.Add(mp);
        if (sp != null && sp.IsConnected) pipes.Add(sp);
        if (pipes.Count == 0) return;

        // WaitForPipeDrain is a blocking native call with no timeout overload;
        // wrap each in Task.Run and bound the total wait with Task.WaitAll.
        // If the timeout expires we proceed anyway — Dispose will tear it down.
        var tasks = pipes.Select(p => Task.Run(() =>
        {
            try { p.WaitForPipeDrain(); }
            catch { /* pipe closed / disconnected from other side — fine */ }
        })).ToArray();

        try { Task.WaitAll(tasks, timeoutMs); }
        catch { /* aggregate exception swallowed; we move on regardless */ }
    }

    private void DisposeCaptures()
    {
        // Flush + dispose pipe/writer INSIDE each lock so any DataAvailable
        // callback currently holding the lock finishes its write first.
        lock (_micLock)
        {
            try { _micWriter?.Flush(); } catch { }
            try { _micWriter?.Dispose(); } catch { }
            _micWriter = null;
            try { _micPipe?.Dispose(); } catch { }
            _micPipe = null;
        }
        lock (_sysLock)
        {
            try { _sysWriter?.Flush(); } catch { }
            try { _sysWriter?.Dispose(); } catch { }
            _sysWriter = null;
            try { _sysPipe?.Dispose(); } catch { }
            _sysPipe = null;
        }

        var silenceOut = _silenceOut;
        _silenceOut = null;
        try { silenceOut?.Dispose(); } catch { }

        // Captures last. WasapiCapture.Dispose() joins its internal thread; if
        // a driver glitch causes NAudio's AudioClient.Dispose() to NRE (seen
        // in Event Viewer reports), the try/catch keeps the recording stop
        // path stable instead of taking down the app.
        var micCapture = _micCapture;
        _micCapture = null;
        try { micCapture?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Audio] mic dispose: {ex.Message}"); }

        var sysCapture = _sysCapture;
        _sysCapture = null;
        try { sysCapture?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Audio] sys dispose: {ex.Message}"); }
    }

    public void Dispose() => Stop();
}
