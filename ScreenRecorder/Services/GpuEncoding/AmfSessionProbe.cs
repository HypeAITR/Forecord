using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenRecorder.Services.GpuEncoding;

public sealed record AmfSessionProbeResult(
    bool CanLoadRuntime,
    bool CanInitializeRuntime,
    bool CanInitializeDx11,
    bool CanInitializeAvc,
    string Message)
{
    public string ToDebugString() =>
        "[GPU encoder] amf-direct session: " +
        $"runtime={CanLoadRuntime}; " +
        $"factory={CanInitializeRuntime}; " +
        $"dx11={CanInitializeDx11}; " +
        $"avc={CanInitializeAvc}; {Message}";
}

public static class AmfSessionProbe
{
    public static AmfSessionProbeResult ProbeDefaultD3D11Device(int width = 64, int height = 64)
    {
        if (!AmfNative.TryCreate(out var api, out var message) || api == null)
            return new AmfSessionProbeResult(false, false, false, false, message);

        using (api)
        using (var device = new Device(
                   DriverType.Hardware,
                   DeviceCreationFlags.BgraSupport,
                   new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }))
        {
            try
            {
                var dx11 = api.ProbeD3D11Avc(device, width, height);
                return new AmfSessionProbeResult(
                    true,
                    true,
                    dx11.CanInitializeAvc,
                    dx11.CanInitializeAvc,
                    $"{message} {dx11.Message}");
            }
            catch (Exception ex)
            {
                return new AmfSessionProbeResult(
                    true,
                    true,
                    false,
                    false,
                    ex.Message);
            }
        }
    }
}
