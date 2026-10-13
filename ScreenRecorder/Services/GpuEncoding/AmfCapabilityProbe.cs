using System.Runtime.InteropServices;

namespace ScreenRecorder.Services.GpuEncoding;

public static class AmfCapabilityProbe
{
    private const string BackendName = "amf-direct";
    private const string LibraryName = "amfrt64.dll";
    private const string QueryVersionExportName = "AMFQueryVersion";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AmfQueryVersionDelegate(out ulong version);

    public static GpuEncoderCapability Probe()
    {
        if (!OperatingSystem.IsWindows())
            return GpuEncoderCapability.Unavailable(BackendName, "AMF is only probed on Windows.");

        if (!Environment.Is64BitProcess)
            return GpuEncoderCapability.Unavailable(BackendName, "AMF direct path requires a 64-bit process.");

        if (!NativeLibrary.TryLoad(LibraryName, out var library))
            return GpuEncoderCapability.Unavailable(BackendName, $"{LibraryName} was not found.");

        try
        {
            if (!NativeLibrary.TryGetExport(library, QueryVersionExportName, out var queryVersionExport))
            {
                return GpuEncoderCapability.Unavailable(
                    BackendName,
                    $"{QueryVersionExportName} export was not found.",
                    canOpenApi: true);
            }

            var queryVersion =
                Marshal.GetDelegateForFunctionPointer<AmfQueryVersionDelegate>(queryVersionExport);
            var status = queryVersion(out var version);
            if (status != 0)
            {
                return GpuEncoderCapability.Unavailable(
                    BackendName,
                    $"{QueryVersionExportName} failed: {AmfNative.StatusName(status)} ({status}).",
                    canOpenApi: true);
            }

            var major = (int)((version >> 48) & 0xFFFF);
            var minor = (int)((version >> 32) & 0xFFFF);
            var release = (int)((version >> 16) & 0xFFFF);
            var build = (int)(version & 0xFFFF);
            return GpuEncoderCapability.Available(
                BackendName,
                $"{LibraryName} is loadable; runtime reports AMF {major}.{minor}.{release}.{build}.",
                unchecked((uint)(version & 0xFFFFFFFF)),
                major,
                minor);
        }
        catch (Exception ex)
        {
            return GpuEncoderCapability.Unavailable(
                BackendName,
                $"AMF probe failed: {ex.Message}",
                canOpenApi: true);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
