using System.Diagnostics;
using ScreenRecorder.Helpers;
using ScreenRecorder.Models;

namespace ScreenRecorder.Services;

public static class SothelCaptureEngine
{
    public const string Name = "Sothel Capture";

    public static string Summary =>
        "Analog, external HDMI/SDI, virtual display, compositor, API interception, " +
        "framebuffer, GPU memory, kernel display, hypervisor and downstream capture " +
        "paths are collapsed into one software-only DWM/DXGI capture gate.";

    public static string Gate =>
        "External hardware, kernel drivers, hypervisors and HDCP-chain dependencies stay disabled; " +
        "Forecord records the composed desktop through Desktop Duplication, then routes frames to " +
        "the active FFmpeg or own-mux encoder path.";

    public static bool IsEnabled(AppSettings settings) =>
        RecordingCaptureMode.IsCapture(settings.CaptureMode);

    internal static SothelCaptureSession? BeginSegment(
        AppSettings settings,
        string pipelineName,
        string segmentPath,
        int width,
        int height)
    {
        if (!IsEnabled(settings))
            return null;

        return new SothelCaptureSession(pipelineName, segmentPath, width, height);
    }
}

internal sealed class SothelCaptureSession : IDisposable
{
    private readonly string _pipelineName;
    private readonly string _segmentPath;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _readbackFrames;
    private long _gpuFrames;
    private long _idleSkips;
    private bool _disposed;

    public SothelCaptureSession(string pipelineName, string segmentPath, int width, int height)
    {
        _pipelineName = pipelineName;
        _segmentPath = segmentPath;

        Debug.WriteLine(
            "[SothelCapture] ACTIVE " +
            $"pipeline={pipelineName} size={width}x{height} path={segmentPath}");
        Debug.WriteLine($"[SothelCapture] {SothelCaptureEngine.Summary}");
        Debug.WriteLine($"[SothelCapture] {SothelCaptureEngine.Gate}");
    }

    public DesktopFrameResult TryGetReadbackFrame(
        DesktopCapture capture,
        byte[] buffer,
        int timeoutMs,
        bool skipDuplicateReadback)
    {
        Interlocked.Increment(ref _readbackFrames);
        var frame = capture.TryGetFrame(buffer, timeoutMs, skipDuplicateReadback);
        TrackSkip(frame);
        return frame;
    }

    public bool TryAcquireGpuFrame(
        DesktopCapture capture,
        out DesktopGpuFrame? frame,
        int timeoutMs)
    {
        Interlocked.Increment(ref _gpuFrames);
        var acquired = capture.TryAcquireGpuFrame(out frame, timeoutMs);
        if (acquired &&
            frame != null &&
            frame.Metadata.LastPresentTime == 0 &&
            frame.Metadata.LastMouseUpdateTime == 0 &&
            !frame.CursorChanged)
        {
            Interlocked.Increment(ref _idleSkips);
        }

        return acquired;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Debug.WriteLine(
            "[SothelCapture] STOP " +
            $"pipeline={_pipelineName} " +
            $"readbackFrames={Interlocked.Read(ref _readbackFrames)} " +
            $"gpuFrames={Interlocked.Read(ref _gpuFrames)} " +
            $"idleCandidates={Interlocked.Read(ref _idleSkips)} " +
            $"elapsedMs={_stopwatch.Elapsed.TotalMilliseconds:F0} " +
            $"path={_segmentPath}");
    }

    private void TrackSkip(DesktopFrameResult frame)
    {
        if (frame.Acquired &&
            !frame.PixelsCopied &&
            !frame.CursorChanged &&
            frame.Metadata.LastPresentTime == 0 &&
            frame.Metadata.LastMouseUpdateTime == 0)
        {
            Interlocked.Increment(ref _idleSkips);
        }
    }
}
