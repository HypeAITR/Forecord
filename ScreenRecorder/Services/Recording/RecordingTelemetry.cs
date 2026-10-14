using System.Diagnostics;
using ScreenRecorder.Helpers;
using ScreenRecorder.Models;

namespace ScreenRecorder.Services.Recording;

internal sealed class RecordingTelemetry
{
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

    public void Reset(int initialFpsCap)
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
        Interlocked.Exchange(ref _adaptiveCurrentFps, initialFpsCap);
        Interlocked.Exchange(ref _adaptiveFpsDrops, 0);
        Interlocked.Exchange(ref _adaptiveFpsRecoveries, 0);
        Interlocked.Exchange(ref _adaptivePressureEvents, 0);
        Interlocked.Exchange(ref _adaptivePresetRequests, 0);
    }

    public void RecordAcquiredFrame(DesktopFrameResult frame, bool useDuplicateFrameSkip)
    {
        Interlocked.Increment(ref _acquiredFrames);
        Interlocked.Add(ref _dxgiAccumulatedFrames, frame.Metadata.AccumulatedFrames);
        Interlocked.Add(ref _dirtyRectCount, frame.Metadata.DirtyRectCount);
        Interlocked.Add(ref _moveRectCount, frame.Metadata.MoveRectCount);
        if (useDuplicateFrameSkip && frame.Metadata.IsMouseOnly)
            Interlocked.Increment(ref _mouseOnlyFrames);

        if (frame.PixelsCopied)
        {
            if (frame.PartialUpdate)
                Interlocked.Increment(ref _partialReadbackFrames);
            else
                Interlocked.Increment(ref _fullReadbackFrames);
            Interlocked.Add(ref _readbackPixels, frame.ReadbackPixels);
        }
    }

    public void RecordSkippedIdleFrame() =>
        Interlocked.Increment(ref _skippedIdleFrames);

    public void RecordEncodedFrame(bool cursorOnly)
    {
        Interlocked.Increment(ref _encodedFrames);
        if (cursorOnly)
            Interlocked.Increment(ref _cursorOnlyEncodedFrames);
    }

    public void RecordSnapshotTaken() =>
        Interlocked.Increment(ref _snapshotsTaken);

    public void RecordAdaptivePressure() =>
        Interlocked.Increment(ref _adaptivePressureEvents);

    public void RecordAdaptiveThrottle(AdaptiveThrottleAction action, int currentFps)
    {
        Interlocked.Exchange(ref _adaptiveCurrentFps, currentFps);
        if (action == AdaptiveThrottleAction.Degraded)
            Interlocked.Increment(ref _adaptiveFpsDrops);
        else if (action == AdaptiveThrottleAction.Recovered)
            Interlocked.Increment(ref _adaptiveFpsRecoveries);
    }

    public void RecordAdaptivePresetRequest() =>
        Interlocked.Increment(ref _adaptivePresetRequests);

    public void LogAndResetSegmentSummary(AppSettings settings, PowerAwareCaptureProfile powerProfile)
    {
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
            $"adaptiveEnabled={settings.EnablePerformanceAdaptiveScaling} " +
            $"powerAware={powerProfile.IsActive} " +
            $"powerBattery={powerProfile.IsBatteryPowered} " +
            $"powerIntegratedGpu={powerProfile.IsIntegratedGraphics} " +
            $"powerFpsCap={powerProfile.FpsCap} " +
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

        Reset(powerProfile.FpsCap);
    }
}
