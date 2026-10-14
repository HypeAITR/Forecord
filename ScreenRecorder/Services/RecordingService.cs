using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ScreenRecorder.Helpers;
using ScreenRecorder.Models;
using ScreenRecorder.Services.Diagnostics;
using ScreenRecorder.Services.Recording;

namespace ScreenRecorder.Services;

public sealed class RecordingService : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    private readonly EncoderService _encoder;
    private readonly AudioService _audio;
    private readonly FileService _files;
    private readonly ScreenshotCaptureCoordinator _screenshots = new();
    private readonly GpuDirectSegmentRunner _gpuDirect = new();
#if DEBUG
    private readonly RecordingTelemetry _telemetry = new();
#endif

    private string _tempDir = "";
    private readonly List<string> _videoSegments = new();
    private readonly List<(string? mic, string? sys)> _audioSegments = new();
    private int _segIdx;

    private Process? _ffmpeg;
    private Thread? _captureThread;
    private volatile bool _stopSignal;
    private int _screenW, _screenH;
    private int _outW, _outH;
    private string _encoderName = "libx264";
    private AppSettings _settings = new();
    private int _adaptivePreferFastPreset;
    private PowerAwareCaptureProfile _powerProfile = PowerAwareCaptureProfile.Disabled(60);

    // Live audio mux: ffmpeg writes a fully-muxed MP4 per segment (instead of
    // separate WAVs that get re-muxed at stop).
    private const bool EnableDebugLiveMux = true;
    // Phase 3.1: VFR stays tied to real elapsed time while idle/duplicate
    // desktop frames are skipped.
    private const bool EnableDebugDuplicateFrameSkip = true;
    private bool _debugLiveMuxActive;

    private readonly SemaphoreSlim _segmentGate = new(1, 1);

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }

    public RecordingService(EncoderService encoder, AudioService audio, FileService files)
    {
        _encoder = encoder;
        _audio = audio;
        _files = files;
    }

    public async Task StartAsync(AppSettings settings)
    {
        settings.EnableGpuDirectPipeline = true;
        settings.HardwareAcceleration = true;
        settings.EnablePerformanceAdaptiveScaling = true;
        _settings = settings;
        _tempDir = _files.CreateTempDirectory();
        _videoSegments.Clear();
        _audioSegments.Clear();
        _segIdx = 0;
        Interlocked.Exchange(ref _adaptivePreferFastPreset, 0);
        _gpuDirect.Reset(settings);
        _debugLiveMuxActive = false;

        await Task.Run(() =>
        {
            GetScreenDimensions(settings.MonitorIndex, out _screenW, out _screenH);
            ComputeOutputDimensions(_screenW, _screenH, settings.OutputResolution, out _outW, out _outH);
            _encoderName = "gpu-direct";
        });

        IsRunning = true;
        IsPaused = false;

        await _segmentGate.WaitAsync();
        try
        {
            await BeginSegmentAsync();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            IsPaused = false;
            RecordingDiagnostics.ReportError(DiagnosticStage.Startup, "Failed to start recording segment.", ex);
            throw;
        }
        finally
        {
            _segmentGate.Release();
        }
    }

    public void Pause()
    {
        _segmentGate.Wait();
        try
        {
            if (!IsRunning || IsPaused) return;
            IsPaused = true;
            EndCurrentSegment();
        }
        finally
        {
            _segmentGate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _segmentGate.WaitAsync();
        try
        {
            if (!IsRunning || !IsPaused) return;
            IsPaused = false;
            await BeginSegmentAsync();
        }
        catch (Exception ex)
        {
            IsPaused = true;
            RecordingDiagnostics.ReportError(DiagnosticStage.Startup, "Failed to resume recording segment.", ex);
            throw;
        }
        finally
        {
            _segmentGate.Release();
        }
    }

    public Task DiscardAsync() => Task.Run(() =>
    {
        _segmentGate.Wait();
        try
        {
            IsRunning = false;
            IsPaused = false;

            _stopSignal = true;
            _captureThread?.Join(8000);
            _captureThread = null;
            _screenshots.CancelPending();
            _audio.Stop();

            try { _ffmpeg?.Kill(); } catch { }
            _ffmpeg?.Dispose();
            _ffmpeg = null;

            _videoSegments.Clear();
            _audioSegments.Clear();
            _segIdx = 0;

            var dir = _tempDir;
            _tempDir = "";
            if (!string.IsNullOrEmpty(dir)) _files.CleanupTemp(dir);
        }
        finally
        {
            _segmentGate.Release();
        }
    });

    public async Task<string> StopAsync()
    {
        await _segmentGate.WaitAsync();
        try
        {
            IsRunning = false;
            IsPaused = false;
            EndCurrentSegment();

            var sizeOk = _videoSegments
                .Where(f => File.Exists(f) && new FileInfo(f).Length > 1024)
                .ToList();

            var validVideoSegments = new List<string>();
            if (_gpuDirect.IsActive)
            {
                validVideoSegments.AddRange(sizeOk);
            }
            else
            {
                foreach (var seg in sizeOk)
                {
                    if (await _encoder.ProbeSegmentAsync(seg))
                        validVideoSegments.Add(seg);
                    else
                        RecordingDiagnostics.ReportError(
                            DiagnosticStage.SegmentProbe,
                            $"Corrupted segment skipped during stop: {seg}");
                }
            }

            if (validVideoSegments.Count == 0)
            {
                if (_gpuDirect.IsActive && !string.IsNullOrWhiteSpace(_gpuDirect.LastError))
                {
                    var muxErr = RecordingDiagnostics.ReportError(
                        DiagnosticStage.MuxFinish,
                        "Kendi mux segmenti olusmadi: " + _gpuDirect.LastError);
                    throw new InvalidOperationException(
                        "Kendi mux segmenti olusmadi.\n" + _gpuDirect.LastError +
                        $"\n(Log: {RecordingDiagnostics.LastErrorFilePath})");
                }

                RecordingDiagnostics.ReportError(
                    DiagnosticStage.SegmentProbe,
                    "No valid video segment was produced.");
                throw new InvalidOperationException("Kaydedilecek saglam video segmenti olusmadi.");
            }

            var outputPath = _files.GenerateOutputPath(_settings.OutputDirectory);

            if (_debugLiveMuxActive)
            {
                var ok = await _encoder.ConcatenateMuxedSegmentsAsync(
                    validVideoSegments, outputPath, _encoderName);
                if (!ok)
                {
                    RecordingDiagnostics.ReportError(
                        DiagnosticStage.SegmentConcat,
                        $"Live mux concatenation failed for output {outputPath}");
                    throw new InvalidOperationException("Dosya yazilamadi (live mux concat basarisiz).");
                }

                _ = Task.Run(() => _files.CleanupTemp(_tempDir));
                return outputPath;
            }

            var micFiles = _audioSegments.Select(a => a.mic)
                .Where(f => f != null && File.Exists(f!)).Cast<string>().ToList();
            var sysFiles = _audioSegments.Select(a => a.sys)
                .Where(f => f != null && File.Exists(f!)).Cast<string>().ToList();

            if (validVideoSegments.Count == 1 && micFiles.Count == 0 && sysFiles.Count == 0)
            {
                File.Copy(validVideoSegments[0], outputPath, true);
                _ = Task.Run(() => _files.CleanupTemp(_tempDir));
                return outputPath;
            }

            if (_gpuDirect.IsActive &&
                micFiles.Count == 0 &&
                sysFiles.Count == 0 &&
                ScreenRecorder.Services.Muxing.H264Mp4Muxer.TryConcatenateSegments(validVideoSegments, outputPath))
            {
                RecordingDiagnostics.Info(
                    DiagnosticStage.SegmentConcat,
                    $"Concatenated {validVideoSegments.Count} GPU-Direct segments natively via H264Mp4Muxer: {outputPath}");
                _ = Task.Run(() => _files.CleanupTemp(_tempDir));
                return outputPath;
            }

            var muxed = await _encoder.MuxFinalAsync(validVideoSegments, micFiles, sysFiles, outputPath);
            if (!muxed)
            {
                RecordingDiagnostics.ReportError(
                    DiagnosticStage.SegmentConcat,
                    $"Final FFmpeg mux failed for output {outputPath}");
                throw new InvalidOperationException("Dosya yazilamadi (ffmpeg birlestirme basarisiz).");
            }

            _ = Task.Run(() => _files.CleanupTemp(_tempDir));
            return outputPath;
        }
        finally
        {
            _segmentGate.Release();
        }
    }

    private async Task BeginSegmentAsync()
    {
        var segPath = Path.Combine(_tempDir, $"seg{_segIdx:D4}.mp4");
        var segDir = Path.Combine(_tempDir, $"audio{_segIdx:D4}");
        Directory.CreateDirectory(segDir);
        _segIdx++;

        var micEnabled = _settings.MicrophoneEnabled;
        var sysEnabled = _settings.SystemAudioEnabled;
        var micId = _settings.MicrophoneDeviceId;
        _powerProfile = PowerAwareCaptureProfile.Detect(
            _settings.Fps,
            _settings.MonitorIndex,
            _settings.EnablePerformanceAdaptiveScaling);
        var usePerformancePreset = _settings.EnablePerformanceAdaptiveScaling &&
                                    (Volatile.Read(ref _adaptivePreferFastPreset) != 0 ||
                                     _powerProfile.UsePerformancePreset);

#if DEBUG
        Debug.WriteLine(_powerProfile.ToDebugString());
        if (usePerformancePreset)
            Debug.WriteLine("[Adaptive] Performance preset is active for this segment.");
#endif

        if (_gpuDirect.TryBeginSegment(segPath, micEnabled, sysEnabled, _screenW, _screenH, _outW, _outH))
        {
            _debugLiveMuxActive = false;
            _videoSegments.Add(segPath);
            _audioSegments.Add((null, null));
            StartCaptureThread();
            return;
        }

        _debugLiveMuxActive = EnableDebugLiveMux && (micEnabled || sysEnabled);
        List<LiveAudioPipeInput>? liveAudioInputs = null;
        var useLiveMux = _debugLiveMuxActive;
        if (useLiveMux)
            liveAudioInputs = _audio.PrepareLivePipes(micEnabled, sysEnabled, micId);

        if (_encoder.FindFFmpeg() == null)
            throw new InvalidOperationException(
                "FFmpeg bulunamadi.\nffmpeg.exe dosyasini uygulama klasorune koyun veya PATH'e ekleyin.");
        if (_encoderName == "nvenc-direct")
            _encoderName = _encoder.DetectBestEncoder(_settings.HardwareAcceleration);

        await Task.Run(() =>
        {
            var ffmpegPipelineName = useLiveMux ? "FFMPEG_LIVE_MUX" : "FFMPEG_PIPE";
            RecordingDiagnostics.Info(DiagnosticStage.EncoderInit, $"ACTIVE={ffmpegPipelineName}");
            _ffmpeg = _encoder.StartVideoEncoding(
                _encoderName, _screenW, _screenH, _settings.Fps, _settings.Quality,
                _outW, _outH, segPath,
                liveAudioInputs,
                forceSilentAudio: useLiveMux && liveAudioInputs?.Count == 0,
                useWallclockVideoTimestamps: EnableDebugDuplicateFrameSkip,
                usePerformancePreset: usePerformancePreset)
                ?? throw new InvalidOperationException("FFmpeg sureci baslatilamadi.");
        });

        if (useLiveMux)
        {
            try
            {
                await _audio.ConnectLivePipesAsync();
                _audio.StartLivePipes();
            }
            catch (Exception ex)
            {
                RecordingDiagnostics.ReportError(
                    DiagnosticStage.AudioPipe,
                    "Failed to connect live audio pipes to FFmpeg.",
                    ex);
                try { _ffmpeg?.Kill(entireProcessTree: true); } catch { }
                try { _ffmpeg?.WaitForExit(2000); } catch { }
                _ffmpeg?.Dispose();
                _ffmpeg = null;
                try { _audio.Stop(); } catch { }
                try { if (File.Exists(segPath)) File.Delete(segPath); } catch { }
                throw;
            }

            _videoSegments.Add(segPath);
            _audioSegments.Add((null, null));
            StartCaptureThread();
            return;
        }

        string? micPath = micEnabled ? Path.Combine(segDir, "mic.wav") : null;
        string? sysPath = sysEnabled ? Path.Combine(segDir, "system.wav") : null;
        _ = Task.Run(() =>
        {
            try { _audio.Start(segDir, micEnabled, sysEnabled, micId); }
            catch (Exception ex)
            {
                RecordingDiagnostics.ReportError(
                    DiagnosticStage.AudioPipe,
                    "Audio capture start failed.",
                    ex);
            }
        });

        _videoSegments.Add(segPath);
        _audioSegments.Add((micPath, sysPath));
        StartCaptureThread();
    }

    private void StartCaptureThread()
    {
        _stopSignal = false;
#if DEBUG
        _telemetry.Reset(_powerProfile.FpsCap);
#endif
        _captureThread = new Thread(CaptureLoop)
        {
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name = $"CaptureThread-{_segIdx}"
        };
        _captureThread.Start();
    }

    public async Task RestartSegmentAsync()
    {
        await _segmentGate.WaitAsync();
        try
        {
            if (!IsRunning || IsPaused) return;
            await Task.Run(EndCurrentSegment);
            await BeginSegmentAsync();
        }
        finally
        {
            _segmentGate.Release();
        }
    }

    // Tears down the current segment. Ordering matters for live-mux: closing
    // the video stdin first signals video EOF, then we let any in-flight audio
    // DataAvailable callbacks drain, then dispose the audio pipes (audio EOF),
    // and only then wait for ffmpeg to write the moov atom and exit.
    private void EndCurrentSegment()
    {
        _stopSignal = true;
        _captureThread?.Join(8000);
        _captureThread = null;
        _screenshots.CancelPending();

#if DEBUG
        _telemetry.LogAndResetSegmentSummary(_settings, _powerProfile);
#endif

        var liveMux = _debugLiveMuxActive;

        if (liveMux)
        {
            try { _audio.StopCaptureFlow(); } catch { }
        }

        try
        {
            _ffmpeg?.StandardInput.BaseStream.Flush();
            _ffmpeg?.StandardInput.BaseStream.Close();
        }
        catch { }

        if (liveMux)
        {
            try { _audio.DrainLivePipes(2500); }
            catch (Exception ex) { Debug.WriteLine($"[Stop] audio drain: {ex.Message}"); }
        }

        try { _audio.Stop(); }
        catch (Exception ex) { Debug.WriteLine($"[Stop] audio dispose: {ex.Message}"); }

        try
        {
            if (_ffmpeg != null && !_ffmpeg.WaitForExit(30_000))
            {
                RecordingDiagnostics.ReportError(
                    DiagnosticStage.MuxFinish,
                    "FFmpeg did not exit within 30s; killing process.");
                try { _ffmpeg.Kill(entireProcessTree: true); } catch { }
                try { _ffmpeg.WaitForExit(2000); } catch { }
            }
        }
        finally
        {
            _ffmpeg?.Dispose();
            _ffmpeg = null;
        }
    }

    private void CaptureLoop()
    {
        timeBeginPeriod(1);

        var useDuplicateFrameSkip = EnableDebugDuplicateFrameSkip;

        DesktopCapture? cap = null;
        try
        {
            cap = new DesktopCapture(_settings.MonitorIndex);
        }
        catch (Exception ex)
        {
            RecordingDiagnostics.ReportError(
                DiagnosticStage.CaptureInit,
                $"DesktopCapture initialization failed on monitor {_settings.MonitorIndex}",
                ex);
            timeEndPeriod(1);
            return;
        }

        if (_gpuDirect.IsActive && _gpuDirect.SegmentPath != null)
        {
            try
            {
                _gpuDirect.RunCaptureLoop(
                    cap,
                    _gpuDirect.SegmentPath,
                    _settings,
                    _powerProfile.FpsCap,
                    _screenshots,
#if DEBUG
                    _telemetry,
#else
                    null,
#endif
                    () => _stopSignal,
                    OnAdaptiveThrottleChanged);
            }
            finally
            {
                cap.Dispose();
                timeEndPeriod(1);
            }
            return;
        }

        var buffer = new byte[cap.Width * cap.Height * 4];

        var adaptive = new AdaptivePerformanceController(
            _settings.Fps,
            _settings.EnablePerformanceAdaptiveScaling,
            _powerProfile.FpsCap);
        var pipe = _ffmpeg?.StandardInput.BaseStream;
        var sw = Stopwatch.StartNew();
        var hasBuffer = false;
        var lastFrameWriteAt = TimeSpan.Zero;
        var maxIdleInterval = TimeSpan.FromMilliseconds(250);

        try
        {
            while (!_stopSignal)
            {
                var t0 = sw.Elapsed;
                var wroteFrame = false;
                var writeDuration = TimeSpan.Zero;
                var initialAttempt = !hasBuffer;
                var acquireTimeoutMs = initialAttempt ? 32 : 0;

                var frame = DesktopFrameResult.None;
                try
                {
                    frame = cap.TryGetFrame(
                        buffer,
                        timeoutMs: acquireTimeoutMs,
                        skipDuplicateReadback: useDuplicateFrameSkip);
                }
                catch (DeviceLostException ex)
                {
                    RecordingDiagnostics.ReportError(
                        DiagnosticStage.CaptureInit,
                        "DXGI device lost during capture; recreating DesktopCapture.",
                        ex);
                    cap.Dispose();
                    Thread.Sleep(200);
                    cap = new DesktopCapture(_settings.MonitorIndex);
                    if (cap.Width != _screenW || cap.Height != _screenH)
                    {
                        RecordingDiagnostics.Info(
                            DiagnosticStage.CaptureInit,
                            "Resolution changed during recording; ending segment.");
                        break;
                    }
                }

                var initialFallbackCopied = false;
                if (!frame.Acquired && initialAttempt && cap.CaptureInitialFrameFallback(buffer))
                {
                    hasBuffer = true;
                    initialFallbackCopied = true;
                }

                if (frame.Acquired)
                {
#if DEBUG
                    _telemetry.RecordAcquiredFrame(frame, useDuplicateFrameSkip);
#endif
                    if (frame.PixelsCopied)
                        hasBuffer = true;
                }

                if (hasBuffer && _screenshots.TryComplete(buffer, cap.Width, cap.Height))
                {
#if DEBUG
                    _telemetry.RecordSnapshotTaken();
#endif
                }

                var frameHasUpdate = initialFallbackCopied ||
                                     (frame.Acquired &&
                                      (!useDuplicateFrameSkip ||
                                       frame.PixelsCopied ||
                                       frame.CursorChanged));
                var idleKeepaliveDue = hasBuffer &&
                                       (sw.Elapsed - lastFrameWriteAt >= maxIdleInterval);
                var shouldWrite = hasBuffer && (frameHasUpdate || idleKeepaliveDue);

#if DEBUG
                if (useDuplicateFrameSkip && frame.Acquired && hasBuffer && !shouldWrite)
                    _telemetry.RecordSkippedIdleFrame();
#endif

                if (shouldWrite && pipe != null)
                {
                    var presentationTime = sw.Elapsed;
                    if (!TryWriteFrameToPipe(cap, buffer, pipe, out writeDuration))
                        break;

                    wroteFrame = true;
                    lastFrameWriteAt = presentationTime;
#if DEBUG
                    var cursorOnly = useDuplicateFrameSkip &&
                                     frame.Acquired &&
                                     frame.CursorChanged &&
                                     !frame.PixelsCopied;
                    _telemetry.RecordEncodedFrame(cursorOnly);
#endif
                }

                var elapsed = sw.Elapsed - t0;
                var action = adaptive.Observe(sw.Elapsed, elapsed, wroteFrame, writeDuration, out var hadPressure);
#if DEBUG
                if (hadPressure)
                    _telemetry.RecordAdaptivePressure();
#endif
                if (action != AdaptiveThrottleAction.None)
                    OnAdaptiveThrottleChanged(action, adaptive.CurrentFps);
                if (adaptive.ShouldUsePerformancePreset)
                    RequestAdaptivePerformancePreset();

                var remaining = adaptive.FrameInterval - elapsed;
                if (remaining > TimeSpan.FromMilliseconds(1))
                    Thread.Sleep((int)remaining.TotalMilliseconds);
            }

            if (hasBuffer && pipe != null)
            {
                if (TryWriteFrameToPipe(cap, buffer, pipe, out _))
                {
#if DEBUG
                    _telemetry.RecordEncodedFrame(cursorOnly: false);
#endif
                }
            }
        }
        finally
        {
            cap.Dispose();
            timeEndPeriod(1);
        }
    }

    private void OnAdaptiveThrottleChanged(AdaptiveThrottleAction action, int currentFps)
    {
#if DEBUG
        _telemetry.RecordAdaptiveThrottle(action, currentFps);
        if (action == AdaptiveThrottleAction.Degraded)
            Debug.WriteLine($"[Adaptive] Backpressure detected, capture cap lowered to {currentFps} fps.");
        else if (action == AdaptiveThrottleAction.Recovered)
            Debug.WriteLine($"[Adaptive] Capture cap recovered to {currentFps} fps.");
#endif
        if (action == AdaptiveThrottleAction.Recovered &&
            currentFps >= _settings.Fps)
        {
            Interlocked.Exchange(ref _adaptivePreferFastPreset, 0);
        }
    }

    private void RequestAdaptivePerformancePreset()
    {
        if (!_settings.EnablePerformanceAdaptiveScaling)
            return;

        if (Interlocked.Exchange(ref _adaptivePreferFastPreset, 1) != 0)
            return;

#if DEBUG
        _telemetry.RecordAdaptivePresetRequest();
        Debug.WriteLine("[Adaptive] Sustained pressure detected; faster encoder preset will be used for the next segment.");
#endif
    }

    private static bool TryWriteFrameToPipe(
        DesktopCapture cap,
        byte[] buffer,
        Stream pipe,
        out TimeSpan writeDuration)
    {
        var sw = Stopwatch.StartNew();
        var composited = cap.CompositeCursor(buffer);
        try
        {
            pipe.Write(buffer, 0, buffer.Length);
            writeDuration = sw.Elapsed;
            return true;
        }
        catch (IOException)
        {
            writeDuration = sw.Elapsed;
            return false;
        }
        catch (ObjectDisposedException)
        {
            writeDuration = sw.Elapsed;
            return false;
        }
        finally
        {
            if (composited)
                cap.RestoreCursor(buffer);
        }
    }

    public Task<bool> TrySaveScreenshotAsync(string path) =>
        _screenshots.CaptureAndSaveAsync(path, IsRunning || IsPaused);

    private static void ComputeOutputDimensions(int srcW, int srcH, string preset,
                                                out int w, out int h)
    {
        var targetH = preset switch
        {
            "720p" => 720,
            "1080p" => 1080,
            "1440p" => 1440,
            "4K" => 2160,
            _ => 0
        };

        if (targetH == 0 || srcH <= 0 || targetH >= srcH)
        {
            w = srcW;
            h = srcH;
            return;
        }

        h = targetH;
        w = (int)Math.Round((double)srcW * targetH / srcH);
        if ((w & 1) == 1) w++;
        if ((h & 1) == 1) h++;
    }

    private static void GetScreenDimensions(int monitorIndex, out int w, out int h)
    {
        using var factory = new SharpDX.DXGI.Factory1();
        using var adapter = factory.GetAdapter1(0);

        var outputCount = 0;
        while (true)
        {
            try { using var o = adapter.GetOutput(outputCount); outputCount++; }
            catch { break; }
        }

        var idx = Math.Min(monitorIndex, Math.Max(0, outputCount - 1));
        using var output = adapter.GetOutput(idx);
        var bounds = output.Description.DesktopBounds;
        w = bounds.Right - bounds.Left;
        h = bounds.Bottom - bounds.Top;
    }

    public void Dispose()
    {
        _stopSignal = true;
        _captureThread?.Join(2000);
        _screenshots.CancelPending();
        try { _ffmpeg?.Kill(); } catch { }
        _ffmpeg?.Dispose();
        _audio.Dispose();
    }
}
