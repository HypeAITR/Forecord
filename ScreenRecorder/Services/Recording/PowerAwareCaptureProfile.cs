namespace ScreenRecorder.Services.Recording;

internal sealed class PowerAwareCaptureProfile
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
