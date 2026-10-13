using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ScreenRecorder.Helpers;
using ScreenRecorder.Models;
using ScreenRecorder.Services.GpuEncoding;
using ScreenRecorder.Services.Muxing;

namespace ScreenRecorder.Services;

public sealed class RecordingService : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    private readonly EncoderService _encoder;
    private readonly AudioService _audio;
    private readonly FileService _files;

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
    private SothelCaptureSession? _sothelCaptureSession;
    private const string DebugGpuDirectMuxEnv = "FORECORD_GPU_DIRECT_MUX";
    private bool _debugGpuDirectMuxRequested;
    private bool _debugGpuDirectMuxActive;
    private string? _debugGpuDirectMuxBackend;
    private string? _debugGpuDirectMuxSegmentPath;
    private string? _debugGpuDirectMuxLastError;
    // Live audio mux: ffmpeg writes a fully-muxed MP4 per segment (instead of
    // separate WAVs that get re-muxed at stop). This collapses the post-stop
    // "Dosya hazırlanıyor…" wait from seconds-to-minutes down to a copy. See
    // EndCurrentSegment for the ordering required to avoid the 48-byte shell
    // bug — the safe path now closes video stdin BEFORE tearing down audio
    // pipes and probes each segment before treating it as valid.
    private const bool EnableDebugLiveMux = true;
    // Phase 3.1: VFR stays tied to real elapsed time while idle/duplicate
    // desktop frames are skipped. This does not change the user's FPS target
    // or encoder preset; it only avoids writing unchanged frames.
    private const bool EnableDebugDuplicateFrameSkip = true;
    private bool _debugLiveMuxActive;

    private volatile bool _screenshotPending;
    private TaskCompletionSource<(byte[] pixels, int w, int h)>? _screenshotTcs;
    private readonly object _screenshotRequestLock = new();
    private readonly SemaphoreSlim _segmentGate = new(1, 1);
#if DEBUG
    private long _acquiredFrames;
    private long _snapshotsTaken;
    private long _encodedFrames;
    private long _mouseOnlyFrames;
    private long _cursorOnlyEncodedFrames;
    private long _skippedIdleFrames;
    private long _fullReadbackFrames;
    private long _partialReadbackFrames;
    private long _readbackPixels;
    private long _dxgiAccumulatedFrames;
    private long _dirtyRectCount;
    private long _moveRectCount;
    private long _captureSegmentStartTicks;
    private long _adaptiveCurrentFps;
    private long _adaptiveFpsDrops;
    private long _adaptiveFpsRecoveries;
    private long _adaptivePressureEvents;
    private long _adaptivePresetRequests;
#endif

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
        settings.CaptureMode = RecordingCaptureMode.Normalize(settings.CaptureMode);
        settings.CaptureTechnique = RecordingCaptureMode.Capture;
        _settings = settings;
        _tempDir = _files.CreateTempDirectory();
        _videoSegments.Clear();
        _audioSegments.Clear();
        _segIdx = 0;
        Interlocked.Exchange(ref _adaptivePreferFastPreset, 0);
        _debugGpuDirectMuxRequested = IsEnvironmentEnabled(DebugGpuDirectMuxEnv) ||
                                      settings.EnableGpuDirectPipeline;
        _debugGpuDirectMuxActive = false;
        _debugGpuDirectMuxBackend = null;
        _debugGpuDirectMuxSegmentPath = null;
        _debugGpuDirectMuxLastError = null;
        EndSothelCaptureSession();
        _debugLiveMuxActive = EnableDebugLiveMux && (settings.MicrophoneEnabled || settings.SystemAudioEnabled);

        if (!_debugGpuDirectMuxRequested && _encoder.FindFFmpeg() == null)
            throw new InvalidOperationException(
                "FFmpeg bulunamadi.\nffmpeg.exe dosyasini uygulama klasorune koyun veya PATH'e ekleyin.");

        await Task.Run(() =>
        {
            GetScreenDimensions(settings.MonitorIndex, out _screenW, out _screenH);
            ComputeOutputDimensions(_screenW, _screenH, settings.OutputResolution, out _outW, out _outH);
            if (!_debugGpuDirectMuxRequested)
                _encoderName = _encoder.DetectBestEncoder(settings.HardwareAcceleration);
            else
                _encoderName = "nvenc-direct";
        });

        IsRunning = true;
        IsPaused = false;

        await _segmentGate.WaitAsync();
        try
        {
            await BeginSegmentAsync();
        }
        catch
        {
            IsRunning = false;
            IsPaused = false;
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
        catch
        {
            IsPaused = true;
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
            CancelPendingScreenshot();
            EndSothelCaptureSession();
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

            // Two-pass filter: cheap size check first, then ffmpeg probe to
            // confirm a decodable video stream. This catches the 48-byte
            // (ftyp-only) shells that file size alone can mistake for valid.
            var sizeOk = _videoSegments
                .Where(f => File.Exists(f) && new FileInfo(f).Length > 1024)
                .ToList();

            var validVideoSegments = new List<string>();
            if (_debugGpuDirectMuxActive)
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
                        Debug.WriteLine($"[Stop] Bozuk segment atlandi: {seg}");
                }
            }

            if (validVideoSegments.Count == 0)
            {
                if (_debugGpuDirectMuxActive && !string.IsNullOrWhiteSpace(_debugGpuDirectMuxLastError))
                    throw new InvalidOperationException(
                        "Kendi mux segmenti olusmadi.\n" + _debugGpuDirectMuxLastError);

                throw new InvalidOperationException("Kaydedilecek saglam video segmenti olusmadi.");
            }

            var outputPath = _files.GenerateOutputPath(_settings.OutputDirectory);

            if (_debugLiveMuxActive)
            {
                var ok = await _encoder.ConcatenateMuxedSegmentsAsync(
                    validVideoSegments, outputPath, _encoderName);
                if (!ok)
                    throw new InvalidOperationException("Dosya yazilamadi (live mux concat basarisiz).");

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

            var muxed = await _encoder.MuxFinalAsync(validVideoSegments, micFiles, sysFiles, outputPath);
            if (!muxed)
                throw new InvalidOperationException("Dosya yazilamadi (ffmpeg birlestirme basarisiz).");

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

        List<LiveAudioPipeInput>? liveAudioInputs = null;
        var useLiveMux = _debugLiveMuxActive;
        if (useLiveMux)
            liveAudioInputs = _audio.PrepareLivePipes(micEnabled, sysEnabled, micId);
#if DEBUG
        Debug.WriteLine(_powerProfile.ToDebugString());
        if (usePerformancePreset)
            Debug.WriteLine("[Adaptive] Performance preset is active for this segment.");
#endif

        if (TryBeginDebugGpuDirectMuxSegment(segPath, micEnabled, sysEnabled))
        {
            _videoSegments.Add(segPath);
            _audioSegments.Add((null, null));
            BeginSothelCaptureSession(OwnMuxPipelineName(), segPath);
            StartCaptureThread();
            return;
        }

        if (_encoder.FindFFmpeg() == null)
            throw new InvalidOperationException(
                "FFmpeg bulunamadi.\nffmpeg.exe dosyasini uygulama klasorune koyun veya PATH'e ekleyin.");
        if (_encoderName == "nvenc-direct")
            _encoderName = _encoder.DetectBestEncoder(_settings.HardwareAcceleration);

        await Task.Run(() =>
        {
            var ffmpegPipelineName = useLiveMux ? "FFMPEG_LIVE_MUX" : "FFMPEG_PIPE";
            Debug.WriteLine($"[Pipeline] ACTIVE={ffmpegPipelineName}");
            _ffmpeg = _encoder.StartVideoEncoding(
                _encoderName, _screenW, _screenH, _settings.Fps, _settings.Quality,
                _outW, _outH, segPath,
                liveAudioInputs,
                forceSilentAudio: useLiveMux && liveAudioInputs?.Count == 0,
                useWallclockVideoTimestamps: EnableDebugDuplicateFrameSkip,
                usePerformancePreset: usePerformancePreset)
                ?? throw new InvalidOperationException("FFmpeg sureci baslatilamadi.");
            BeginSothelCaptureSession(ffmpegPipelineName, segPath);
        });

        if (useLiveMux)
        {
            try
            {
                await _audio.ConnectLivePipesAsync();
                _audio.StartLivePipes();
            }
            catch
            {
                EndSothelCaptureSession();
                // Tear ffmpeg down first so it releases its handle to segPath,
                // then we can delete the 48-byte shell it left on disk so the
                // probe pass at stop never sees it.
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
            catch (Exception ex) { Debug.WriteLine($"[Audio] Start hatasi: {ex.Message}"); }
        });

        _videoSegments.Add(segPath);
        _audioSegments.Add((micPath, sysPath));
        StartCaptureThread();
    }

    private void StartCaptureThread()
    {
        _stopSignal = false;
#if DEBUG
        ResetDebugTelemetry();
#endif
        _captureThread = new Thread(CaptureLoop)
        {
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name = $"CaptureThread-{_segIdx}"
        };
        _captureThread.Start();
    }

#if DEBUG
    private void ResetDebugTelemetry()
    {
        Interlocked.Exchange(ref _acquiredFrames, 0);
        Interlocked.Exchange(ref _snapshotsTaken, 0);
        Interlocked.Exchange(ref _encodedFrames, 0);
        Interlocked.Exchange(ref _mouseOnlyFrames, 0);
        Interlocked.Exchange(ref _cursorOnlyEncodedFrames, 0);
        Interlocked.Exchange(ref _skippedIdleFrames, 0);
        Interlocked.Exchange(ref _fullReadbackFrames, 0);
        Interlocked.Exchange(ref _partialReadbackFrames, 0);
        Interlocked.Exchange(ref _readbackPixels, 0);
        Interlocked.Exchange(ref _dxgiAccumulatedFrames, 0);
        Interlocked.Exchange(ref _dirtyRectCount, 0);
        Interlocked.Exchange(ref _moveRectCount, 0);
        Interlocked.Exchange(ref _captureSegmentStartTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _adaptiveCurrentFps, _powerProfile.FpsCap);
        Interlocked.Exchange(ref _adaptiveFpsDrops, 0);
        Interlocked.Exchange(ref _adaptiveFpsRecoveries, 0);
        Interlocked.Exchange(ref _adaptivePressureEvents, 0);
        Interlocked.Exchange(ref _adaptivePresetRequests, 0);
    }
#endif

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
    // and only then wait for ffmpeg to write the moov atom and exit. Stopping
    // the audio pipes before video EOF was the cause of 48-byte segment shells
    // — ffmpeg exited before writing moov and the file stayed at ftyp only.
    private void EndCurrentSegment()
    {
        // 1. Stop the video capture thread. After Join, no more pipe writes
        //    can come from us.
        _stopSignal = true;
        _captureThread?.Join(8000);
        _captureThread = null;
        CancelPendingScreenshot();
        EndSothelCaptureSession();

#if DEBUG
        var telemetryTicks = Interlocked.Read(ref _captureSegmentStartTicks);
        var telemetrySeconds = telemetryTicks > 0
            ? (Stopwatch.GetTimestamp() - telemetryTicks) / (double)Stopwatch.Frequency
            : 0.0;
        var encodedFrames = Interlocked.Read(ref _encodedFrames);
        var effectiveFps = telemetrySeconds > 0.001
            ? encodedFrames / telemetrySeconds
            : 0.0;

        Debug.WriteLine(
            $"[VFR] segment acquired={Interlocked.Read(ref _acquiredFrames)} " +
            $"snapshots={Interlocked.Read(ref _snapshotsTaken)} " +
            $"encoded={encodedFrames} " +
            $"skippedIdle={Interlocked.Read(ref _skippedIdleFrames)} " +
            $"mouseOnly={Interlocked.Read(ref _mouseOnlyFrames)} " +
            $"cursorOnlyEncoded={Interlocked.Read(ref _cursorOnlyEncodedFrames)} " +
            $"effectiveFps={effectiveFps:F2} " +
            $"adaptiveEnabled={_settings.EnablePerformanceAdaptiveScaling} " +
            $"powerAware={_powerProfile.IsActive} " +
            $"powerBattery={_powerProfile.IsBatteryPowered} " +
            $"powerIntegratedGpu={_powerProfile.IsIntegratedGraphics} " +
            $"powerFpsCap={_powerProfile.FpsCap} " +
            $"adaptiveFps={Interlocked.Read(ref _adaptiveCurrentFps)} " +
            $"adaptiveDrops={Interlocked.Read(ref _adaptiveFpsDrops)} " +
            $"adaptiveRecoveries={Interlocked.Read(ref _adaptiveFpsRecoveries)} " +
            $"adaptivePressure={Interlocked.Read(ref _adaptivePressureEvents)} " +
            $"adaptivePresetRequests={Interlocked.Read(ref _adaptivePresetRequests)} " +
            $"dxgiAccumulated={Interlocked.Read(ref _dxgiAccumulatedFrames)} " +
            $"dirtyRects={Interlocked.Read(ref _dirtyRectCount)} " +
            $"moveRects={Interlocked.Read(ref _moveRectCount)} " +
            $"fullReadback={Interlocked.Read(ref _fullReadbackFrames)} " +
            $"partialReadback={Interlocked.Read(ref _partialReadbackFrames)} " +
            $"readbackPixels={Interlocked.Read(ref _readbackPixels)}");
        ResetDebugTelemetry();
#endif

        var liveMux = _debugLiveMuxActive;

        // 2. Tell NAudio to stop capturing. DataAvailable may still fire once
        //    or twice for buffered samples; that's fine — they get written to
        //    the still-open audio pipe and ffmpeg will mux them.
        if (liveMux)
        {
            try { _audio.StopCaptureFlow(); } catch { }
        }

        // 3. Close ffmpeg's video stdin → ffmpeg sees video EOF. With audio
        //    pipes still open, ffmpeg can finish reading any buffered audio
        //    before it finalises the file.
        try
        {
            _ffmpeg?.StandardInput.BaseStream.Flush();
            _ffmpeg?.StandardInput.BaseStream.Close();
        }
        catch { }

        // 3b. Live-mux only: wait for ffmpeg to drain the kernel pipe buffer
        //     of audio bytes BEFORE we dispose the pipe. With the 1 MB out
        //     buffer added for back-pressure, abrupt Dispose can drop up to
        //     ~2.7 s of audio at the tail — heard as cut/choppy ending and
        //     a malformed last AAC frame that breaks player parsing midway
        //     through playback (e.g. 0x80070323 in Films & TV).
        if (liveMux)
        {
            try { _audio.DrainLivePipes(2500); }
            catch (Exception ex) { Debug.WriteLine($"[Stop] audio drain: {ex.Message}"); }
        }

        // 4. Dispose audio resources (pipes + writers + captures). In live-mux
        //    mode this signals audio EOF to ffmpeg, unblocking its writer
        //    thread so it can write moov and exit.
        try { _audio.Stop(); }
        catch (Exception ex) { Debug.WriteLine($"[Stop] audio dispose: {ex.Message}"); }

        // 5. Wait for ffmpeg to finalise the MP4. Bumped from 10 s to 30 s:
        //    with aresample async filter + AAC encoder drain + B-frame
        //    lookahead + moov-at-end write, a clean exit can legitimately take
        //    several seconds on slower disks. Hitting Kill() here means moov
        //    is never written → the MP4 has frames but no index, players
        //    play what they can recover then error out (the symptom we saw).
        try
        {
            if (_ffmpeg != null && !_ffmpeg.WaitForExit(30_000))
            {
                Debug.WriteLine("[Stop] ffmpeg did not exit in 30s; killing.");
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
            Debug.WriteLine($"[Capture] Baslatma hatasi: {ex.Message}");
            timeEndPeriod(1);
            return;
        }

        if (_debugGpuDirectMuxActive && _debugGpuDirectMuxSegmentPath != null)
        {
            try
            {
                CaptureGpuDirectMuxLoop(cap, _debugGpuDirectMuxSegmentPath);
            }
            finally
            {
                cap.Dispose();
                timeEndPeriod(1);
            }
            return;
        }

        var buffer = new byte[cap.Width * cap.Height * 4];
        if (_settings.EnableGpuDirectPipeline &&
            !TryProbeGpuDirectCaptureContract(cap))
        {
            Debug.WriteLine("[GpuDirect] Phase 2A probe failed; using FFmpeg pipe fallback.");
        }

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
                    frame = _sothelCaptureSession?.TryGetReadbackFrame(
                                cap,
                                buffer,
                                timeoutMs: acquireTimeoutMs,
                                skipDuplicateReadback: useDuplicateFrameSkip)
                            ?? cap.TryGetFrame(
                                buffer,
                                timeoutMs: acquireTimeoutMs,
                                skipDuplicateReadback: useDuplicateFrameSkip);
                }
                catch (DeviceLostException)
                {
                    cap.Dispose();
                    Thread.Sleep(200);
                    cap = new DesktopCapture(_settings.MonitorIndex);
                    if (cap.Width != _screenW || cap.Height != _screenH)
                    {
                        Debug.WriteLine("[Capture] Resolution changed during recording, ending segment.");
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
                    Interlocked.Increment(ref _acquiredFrames);
                    Interlocked.Add(ref _dxgiAccumulatedFrames, frame.Metadata.AccumulatedFrames);
                    Interlocked.Add(ref _dirtyRectCount, frame.Metadata.DirtyRectCount);
                    Interlocked.Add(ref _moveRectCount, frame.Metadata.MoveRectCount);
                    if (useDuplicateFrameSkip && frame.Metadata.IsMouseOnly)
                        Interlocked.Increment(ref _mouseOnlyFrames);
#endif

                    if (frame.PixelsCopied)
                    {
                        hasBuffer = true;

#if DEBUG
                        if (frame.PartialUpdate)
                            Interlocked.Increment(ref _partialReadbackFrames);
                        else
                            Interlocked.Increment(ref _fullReadbackFrames);
                        Interlocked.Add(ref _readbackPixels, frame.ReadbackPixels);
#endif
                    }
                }

                if (hasBuffer)
                    TryCompletePendingScreenshot(buffer, cap.Width, cap.Height);

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
                    Interlocked.Increment(ref _skippedIdleFrames);
#endif

                if (shouldWrite && pipe != null)
                {
                    var presentationTime = sw.Elapsed;
                    if (!TryWriteFrameToPipe(cap, buffer, pipe, out writeDuration))
                        break;

                    wroteFrame = true;
                    lastFrameWriteAt = presentationTime;
#if DEBUG
                    Interlocked.Increment(ref _encodedFrames);
                    if (useDuplicateFrameSkip && frame.Acquired && frame.CursorChanged && !frame.PixelsCopied)
                        Interlocked.Increment(ref _cursorOnlyEncodedFrames);
#endif
                }

                var elapsed = sw.Elapsed - t0;
                var action = adaptive.Observe(sw.Elapsed, elapsed, wroteFrame, writeDuration, out var hadPressure);
#if DEBUG
                if (hadPressure)
                    Interlocked.Increment(ref _adaptivePressureEvents);
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
                    Interlocked.Increment(ref _encodedFrames);
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

    private bool TryBeginDebugGpuDirectMuxSegment(string segPath, bool micEnabled, bool sysEnabled)
    {
        _debugGpuDirectMuxActive = false;
        _debugGpuDirectMuxBackend = null;
        _debugGpuDirectMuxSegmentPath = null;

        if (!_debugGpuDirectMuxRequested)
            return false;

        var forcedByEnv = IsEnvironmentEnabled(DebugGpuDirectMuxEnv);
        if (micEnabled || sysEnabled)
        {
            if (!forcedByEnv)
            {
                Debug.WriteLine("[Pipeline] GPU direct own mux requested, but audio is enabled; using FFmpeg path.");
                return false;
            }

            Debug.WriteLine("[Pipeline] GPU direct own mux env is set; audio is bypassed for this video-only mux test.");
        }

        if (_outW != _screenW || _outH != _screenH)
        {
            Debug.WriteLine("[Pipeline] GPU direct own mux requested, but output scaling is enabled; using FFmpeg path.");
            return false;
        }

        DesktopAdapterInfo adapterInfo;
        try
        {
            adapterInfo = DesktopCapture.GetDefaultAdapterInfo();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Pipeline] Could not inspect capture adapter for GPU direct own mux: {ex.Message}; using FFmpeg path.");
            return false;
        }

        Debug.WriteLine($"[Pipeline] GPU direct own mux candidate adapter={adapterInfo.ToDebugString()}");
        if (!adapterInfo.IsNvidia)
        {
            if (adapterInfo.IsAmd)
            {
                var amfProbe = AmfSessionProbe.ProbeDefaultD3D11Device(_screenW, _screenH);
                Debug.WriteLine(amfProbe.ToDebugString());
                if (!amfProbe.CanInitializeAvc)
                {
                    Debug.WriteLine("[Pipeline] AMF AVC backend probe did not pass. Using FFmpeg path.");
                    return false;
                }

                _debugGpuDirectMuxActive = true;
                _debugGpuDirectMuxBackend = "amf-direct";
                _debugGpuDirectMuxSegmentPath = segPath;
                _debugGpuDirectMuxLastError = null;
                Debug.WriteLine("[Pipeline] ACTIVE=GPU_DIRECT_AMF_OWN_MUX videoOnly=true container=mp4");
                return true;
            }

            Debug.WriteLine(
                "[Pipeline] GPU direct own mux currently has only the NVENC backend; " +
                $"capture adapter is {adapterInfo.VendorName}. Using FFmpeg path until the matching native backend is added.");
            return false;
        }

        var nvencProbe = NvencSessionProbe.ProbeDefaultD3D11Device();
        Debug.WriteLine(nvencProbe.ToDebugString());
        if (!nvencProbe.CanOpenD3D11Session ||
            !nvencProbe.SupportsH264 ||
            !nvencProbe.SupportsArgbInput ||
            !nvencProbe.CanInitializeEncoder)
        {
            Debug.WriteLine("[Pipeline] NVENC own mux probe did not pass; using FFmpeg path.");
            return false;
        }

        _debugGpuDirectMuxActive = true;
        _debugGpuDirectMuxBackend = "nvenc-direct";
        _debugGpuDirectMuxSegmentPath = segPath;
        _debugGpuDirectMuxLastError = null;
        Debug.WriteLine("[Pipeline] ACTIVE=GPU_DIRECT_NVENC_OWN_MUX videoOnly=true container=mp4");
        return true;
    }

    private void CaptureGpuDirectMuxLoop(DesktopCapture cap, string segmentPath)
    {
        var adaptive = new AdaptivePerformanceController(
            _settings.Fps,
            _settings.EnablePerformanceAdaptiveScaling,
            _powerProfile.FpsCap);
        var sw = Stopwatch.StartNew();
        long acquiredFrames = 0;
        long skippedFrames = 0;
        long encodedCallbacks = 0;

        try
        {
            using var muxer = new H264Mp4Muxer(segmentPath, cap.Width, cap.Height, _settings.Fps);
            using var encoder = CreateDebugGpuDirectEncoder();

            void AddEncodedPacket(byte[]? bitstream, long outputTimeStamp, long outputDuration, string pictureType)
            {
                if (bitstream is not { Length: > 0 })
                    return;

                var pts = outputTimeStamp >= 0 && outputTimeStamp <= long.MaxValue
                    ? TimeSpan.FromTicks(outputTimeStamp)
                    : sw.Elapsed;
                var duration = outputDuration > 0 && outputDuration <= long.MaxValue
                    ? TimeSpan.FromTicks(outputDuration)
                    : TimeSpan.Zero;

                muxer.AddAnnexBPacket(bitstream, pts, duration, pictureType);
                Interlocked.Increment(ref encodedCallbacks);
            }

            if (encoder is NvencGpuEncoder nvencEncoder)
            {
                nvencEncoder.PacketEncoded += packet =>
                    AddEncodedPacket(
                        packet.Bitstream,
                        packet.OutputTimeStamp <= long.MaxValue ? (long)packet.OutputTimeStamp : -1,
                        packet.OutputDuration <= long.MaxValue ? (long)packet.OutputDuration : 0,
                        packet.PictureType);
            }
            else if (encoder is AmfGpuEncoder amfEncoder)
            {
                amfEncoder.PacketEncoded += packet =>
                    AddEncodedPacket(
                        packet.Bitstream,
                        packet.OutputTimeStamp,
                        packet.OutputDuration,
                        packet.PictureType);
            }

            encoder.Initialize(
                cap.Device,
                new GpuEncoderSettings(
                    cap.Width,
                    cap.Height,
                    _settings.Fps,
                    _settings.Quality,
                    segmentPath));

            var encodedAny = false;
            while (!_stopSignal)
            {
                var t0 = sw.Elapsed;
                var wroteFrame = false;

                try
                {
                    DesktopGpuFrame? gpuFrame;
                    var acquiredGpuFrame = _sothelCaptureSession != null
                        ? _sothelCaptureSession.TryAcquireGpuFrame(cap, out gpuFrame, timeoutMs: 0)
                        : cap.TryAcquireGpuFrame(out gpuFrame, timeoutMs: 0);

                    if (acquiredGpuFrame &&
                        gpuFrame != null)
                    {
                        using (gpuFrame)
                        {
                            acquiredFrames++;
                            var hasImageUpdate = gpuFrame.Metadata.LastPresentTime != 0;
                            var primeEncoder = muxer.SampleCount == 0 && acquiredFrames <= 30;
                            if (!encodedAny || hasImageUpdate || primeEncoder)
                            {
                                encoder.EncodeTexture(gpuFrame.Texture, sw.Elapsed);
                                encodedAny = true;
                                wroteFrame = true;
                            }
                            else
                            {
                                skippedFrames++;
                            }
                        }
                    }
                }
                catch (DeviceLostException)
                {
                    throw;
                }

                var elapsed = sw.Elapsed - t0;
                var action = adaptive.Observe(sw.Elapsed, elapsed, wroteFrame, TimeSpan.Zero, out _);
                if (action != AdaptiveThrottleAction.None)
                    OnAdaptiveThrottleChanged(action, adaptive.CurrentFps);

                var remaining = adaptive.FrameInterval - elapsed;
                if (remaining > TimeSpan.FromMilliseconds(1))
                    Thread.Sleep((int)remaining.TotalMilliseconds);
            }

            encoder.FinishAsync().GetAwaiter().GetResult();
            muxer.Finish();
            Debug.WriteLine(
                $"[OwnMux] segment finished acquired={acquiredFrames} " +
                $"encodedPackets={Interlocked.Read(ref encodedCallbacks)} " +
                $"skippedIdle={skippedFrames} " +
                $"path={segmentPath}");
        }
        catch (Exception ex)
        {
            _debugGpuDirectMuxLastError = ex.Message;
            Debug.WriteLine($"[OwnMux] GPU direct mux failed: {ex}");
            try { File.WriteAllText(segmentPath + ".ownmux.error.txt", ex.ToString()); } catch { }
            try { if (File.Exists(segmentPath)) File.Delete(segmentPath); } catch { }
        }
    }

    private IGpuEncoder CreateDebugGpuDirectEncoder() =>
        string.Equals(_debugGpuDirectMuxBackend, "amf-direct", StringComparison.OrdinalIgnoreCase)
            ? new AmfGpuEncoder()
            : new NvencGpuEncoder();

    private string OwnMuxPipelineName() =>
        string.Equals(_debugGpuDirectMuxBackend, "amf-direct", StringComparison.OrdinalIgnoreCase)
            ? "GPU_DIRECT_AMF_OWN_MUX"
            : "GPU_DIRECT_NVENC_OWN_MUX";

    private void BeginSothelCaptureSession(string pipelineName, string segmentPath)
    {
        EndSothelCaptureSession();
        _sothelCaptureSession = SothelCaptureEngine.BeginSegment(
            _settings,
            pipelineName,
            segmentPath,
            _screenW,
            _screenH);
    }

    private void EndSothelCaptureSession()
    {
        _sothelCaptureSession?.Dispose();
        _sothelCaptureSession = null;
    }

    private bool TryProbeGpuDirectCaptureContract(DesktopCapture cap)
    {
        try
        {
            if (!cap.TryAcquireGpuFrame(out var gpuFrame, timeoutMs: 0) ||
                gpuFrame == null)
            {
                Debug.WriteLine("[GpuDirect] Phase 2A probe did not acquire a GPU frame yet.");
                return false;
            }

            using (gpuFrame)
            {
                var desc = gpuFrame.Texture.Description;
                Debug.WriteLine(
                    "[GpuDirect] Phase 2A GPU frame contract ok " +
                    $"size={gpuFrame.Width}x{gpuFrame.Height} " +
                    $"format={desc.Format} " +
                    $"lastPresent={gpuFrame.Metadata.LastPresentTime} " +
                    $"dirtyRects={gpuFrame.Metadata.DirtyRectCount} " +
                    $"moveRects={gpuFrame.Metadata.MoveRectCount}");

                using var encoder = new NvencGpuEncoder();
                if (!encoder.Capability.IsAvailable)
                {
                    Debug.WriteLine($"[GpuDirect] Phase 2B NVENC unavailable: {encoder.Capability.Message}");
                    return false;
                }

                encoder.Initialize(
                    cap.Device,
                    new GpuEncoderSettings(
                        gpuFrame.Width,
                        gpuFrame.Height,
                        _settings.Fps,
                        _settings.Quality,
                        ""));
                encoder.EncodeTexture(gpuFrame.Texture, TimeSpan.Zero);
                Debug.WriteLine("[GpuDirect] Phase 2B NVENC live desktop texture register/map smoke succeeded.");
                return true;
            }
        }
        catch (DeviceLostException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GpuDirect] Phase 2A probe error: {ex.Message}");
            return false;
        }
    }

    private void OnAdaptiveThrottleChanged(AdaptiveThrottleAction action, int currentFps)
    {
#if DEBUG
        Interlocked.Exchange(ref _adaptiveCurrentFps, currentFps);
        if (action == AdaptiveThrottleAction.Degraded)
        {
            Interlocked.Increment(ref _adaptiveFpsDrops);
            Debug.WriteLine($"[Adaptive] Backpressure detected, capture cap lowered to {currentFps} fps.");
        }
        else if (action == AdaptiveThrottleAction.Recovered)
        {
            Interlocked.Increment(ref _adaptiveFpsRecoveries);
            Debug.WriteLine($"[Adaptive] Capture cap recovered to {currentFps} fps.");
        }
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
        Interlocked.Increment(ref _adaptivePresetRequests);
        Debug.WriteLine("[Adaptive] Sustained pressure detected; faster encoder preset will be used for the next segment.");
#endif
    }

    private static bool IsEnvironmentEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value != null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private enum AdaptiveThrottleAction
    {
        None,
        Degraded,
        Recovered
    }

    private sealed class AdaptivePerformanceController
    {
        private readonly bool _enabled;
        private readonly int[] _fpsLevels;
        private int _levelIndex;
        private int _pressureScore;
        private int _calmScore;
        private TimeSpan _lastChangeAt;

        public AdaptivePerformanceController(int selectedFps, bool enabled, int fpsCap)
        {
            _enabled = enabled;
            var cappedFps = enabled
                ? Math.Clamp(fpsCap, 1, Math.Max(1, selectedFps))
                : Math.Max(1, selectedFps);
            _fpsLevels = BuildFpsLevels(cappedFps);
        }

        public int CurrentFps => _fpsLevels[_levelIndex];

        public TimeSpan FrameInterval =>
            TimeSpan.FromSeconds(1.0 / Math.Max(1, CurrentFps));

        public bool ShouldUsePerformancePreset =>
            _enabled &&
            _fpsLevels.Length > 1 &&
            CurrentFps <= Math.Min(45, _fpsLevels[0]);

        public AdaptiveThrottleAction Observe(
            TimeSpan now,
            TimeSpan loopElapsed,
            bool wroteFrame,
            TimeSpan writeDuration,
            out bool hasPressure)
        {
            hasPressure = false;
            if (!_enabled || _fpsLevels.Length <= 1)
                return AdaptiveThrottleAction.None;

            var interval = FrameInterval;
            var writePressureThreshold = TimeSpan.FromMilliseconds(
                Math.Max(6.0, interval.TotalMilliseconds * 0.65));
            hasPressure =
                (wroteFrame && writeDuration > writePressureThreshold) ||
                loopElapsed > TimeSpan.FromMilliseconds(interval.TotalMilliseconds * 1.35);

            if (hasPressure)
            {
                _pressureScore++;
                _calmScore = 0;
            }
            else
            {
                _calmScore++;
                if (_pressureScore > 0)
                    _pressureScore--;
            }

            if (hasPressure && _pressureScore >= 10 &&
                _levelIndex < _fpsLevels.Length - 1 &&
                now - _lastChangeAt > TimeSpan.FromSeconds(2))
            {
                _levelIndex++;
                _pressureScore = 0;
                _lastChangeAt = now;
                return AdaptiveThrottleAction.Degraded;
            }

            if (!hasPressure && _levelIndex > 0 &&
                _calmScore >= Math.Max(90, CurrentFps * 8) &&
                now - _lastChangeAt > TimeSpan.FromSeconds(8))
            {
                _levelIndex--;
                _calmScore = 0;
                _lastChangeAt = now;
                return AdaptiveThrottleAction.Recovered;
            }

            return AdaptiveThrottleAction.None;
        }

        private static int[] BuildFpsLevels(int selectedFps)
        {
            var baseFps = Math.Max(1, selectedFps);
            var levels = new List<int> { baseFps };
            foreach (var candidate in new[] { 90, 60, 45, 30 })
            {
                if (candidate < baseFps && !levels.Contains(candidate))
                    levels.Add(candidate);
            }

            return levels.OrderByDescending(fps => fps).ToArray();
        }
    }

    private sealed class PowerAwareCaptureProfile
    {
        private PowerAwareCaptureProfile(
            bool isEnabled,
            bool isBatteryPowered,
            bool isIntegratedGraphics,
            int selectedFps,
            int fpsCap,
            bool usePerformancePreset,
            string adapterName)
        {
            IsEnabled = isEnabled;
            IsBatteryPowered = isBatteryPowered;
            IsIntegratedGraphics = isIntegratedGraphics;
            SelectedFps = selectedFps;
            FpsCap = Math.Clamp(fpsCap, 1, Math.Max(1, selectedFps));
            UsePerformancePreset = usePerformancePreset;
            AdapterName = adapterName;
        }

        public bool IsEnabled { get; }
        public bool IsBatteryPowered { get; }
        public bool IsIntegratedGraphics { get; }
        public int SelectedFps { get; }
        public int FpsCap { get; }
        public bool UsePerformancePreset { get; }
        public string AdapterName { get; }
        public bool IsActive => IsEnabled && IsBatteryPowered && IsIntegratedGraphics;

        public static PowerAwareCaptureProfile Disabled(int selectedFps) =>
            new(false, false, false, Math.Max(1, selectedFps), Math.Max(1, selectedFps), false, "");

        public static PowerAwareCaptureProfile Detect(int selectedFps, int monitorIndex, bool enabled)
        {
            selectedFps = Math.Max(1, selectedFps);
            if (!enabled)
                return Disabled(selectedFps);

            var isBatteryPowered = IsRunningOnBattery();
            var (isIntegratedGraphics, adapterName) = DetectIntegratedGraphics(monitorIndex);
            var isActive = isBatteryPowered && isIntegratedGraphics;
            var fpsCap = isActive ? Math.Min(selectedFps, 30) : selectedFps;

            return new PowerAwareCaptureProfile(
                true,
                isBatteryPowered,
                isIntegratedGraphics,
                selectedFps,
                fpsCap,
                usePerformancePreset: isActive,
                adapterName);
        }

        public string ToDebugString()
        {
            if (!IsEnabled)
                return "[PowerAware] disabled by adaptive scaling setting.";

            return "[PowerAware] " +
                   $"battery={IsBatteryPowered} " +
                   $"integratedGpu={IsIntegratedGraphics} " +
                   $"selectedFps={SelectedFps} " +
                   $"fpsCap={FpsCap} " +
                   $"performancePreset={UsePerformancePreset} " +
                   $"adapter=\"{AdapterName}\"";
        }

        private static bool IsRunningOnBattery()
        {
            try
            {
                return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                       System.Windows.Forms.PowerLineStatus.Offline;
            }
            catch
            {
                return false;
            }
        }

        private static (bool isIntegrated, string adapterName) DetectIntegratedGraphics(int monitorIndex)
        {
            try
            {
                using var factory = new SharpDX.DXGI.Factory1();
                using var adapter = factory.GetAdapter1(0);
                var desc = adapter.Description1;
                var name = desc.Description?.Trim() ?? "";
                var dedicatedMemory = desc.DedicatedVideoMemory;
                var vendorId = desc.VendorId;

                var looksIntegrated =
                    vendorId == 0x8086 ||
                    dedicatedMemory <= 512L * 1024L * 1024L ||
                    name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("UHD", StringComparison.OrdinalIgnoreCase);

                return (looksIntegrated, name);
            }
            catch
            {
                return (false, "");
            }
        }
    }

    private void TryCompletePendingScreenshot(byte[] buffer, int width, int height)
    {
        if (!_screenshotPending)
            return;

        _screenshotPending = false;
        var tcs = Volatile.Read(ref _screenshotTcs);
        if (tcs == null)
            return;

        var snap = new byte[buffer.Length];
        Buffer.BlockCopy(buffer, 0, snap, 0, buffer.Length);
        if (tcs.TrySetResult((snap, width, height)))
        {
#if DEBUG
            Interlocked.Increment(ref _snapshotsTaken);
#endif
        }
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

    private void CancelPendingScreenshot()
    {
        TaskCompletionSource<(byte[] pixels, int w, int h)>? tcs;
        lock (_screenshotRequestLock)
        {
            tcs = _screenshotTcs;
            _screenshotTcs = null;
            _screenshotPending = false;
        }

        tcs?.TrySetCanceled();
    }

    public async Task<bool> TrySaveScreenshotAsync(string path)
    {
        if (!IsRunning && !IsPaused)
            return false;

        var tcs = new TaskCompletionSource<(byte[] pixels, int w, int h)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_screenshotRequestLock)
        {
            if (_screenshotTcs != null)
                return false;

            _screenshotTcs = tcs;
            _screenshotPending = true;
        }

        try
        {
            var (pixels, w, h) = await tcs.Task
                .WaitAsync(TimeSpan.FromMilliseconds(1000))
                .ConfigureAwait(false);

            WriteSnapshotPng(pixels, w, h, path);
            return true;
        }
        catch (TimeoutException)
        {
            tcs.TrySetCanceled();
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_screenshotRequestLock)
            {
                if (ReferenceEquals(_screenshotTcs, tcs))
                {
                    _screenshotTcs = null;
                    _screenshotPending = false;
                }
            }
        }
    }

    private static void WriteSnapshotPng(byte[] pixels, int w, int h, string path)
    {
        using var bmp = new System.Drawing.Bitmap(w, h,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = w * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            else
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * rowBytes,
                        IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void ComputeOutputDimensions(int srcW, int srcH, string preset,
                                                out int w, out int h)
    {
        var targetH = preset switch
        {
            "1080p" => 1080,
            "1440p" => 1440,
            "4K" => 2160,
            _ => 0
        };

        if (targetH == 0 || srcH <= 0)
        {
            w = srcW;
            h = srcH;
            return;
        }

        if (targetH >= srcH)
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
        CancelPendingScreenshot();
        EndSothelCaptureSession();
        try { _ffmpeg?.Kill(); } catch { }
        _ffmpeg?.Dispose();
        _audio.Dispose();
    }
}
