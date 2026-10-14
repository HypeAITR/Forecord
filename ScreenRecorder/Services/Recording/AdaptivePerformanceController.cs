namespace ScreenRecorder.Services.Recording;

internal enum AdaptiveThrottleAction
{
    None,
    Degraded,
    Recovered
}

internal sealed class AdaptivePerformanceController
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
