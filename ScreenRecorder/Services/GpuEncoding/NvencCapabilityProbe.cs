using System.Runtime.InteropServices;

namespace ScreenRecorder.Services.GpuEncoding;

public static class NvencCapabilityProbe
{
    private const string BackendName = "nvenc-direct";
    private const string LibraryName = "nvEncodeAPI64.dll";
    private const string VersionExportName = "NvEncodeAPIGetMaxSupportedVersion";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEncodeApiGetMaxSupportedVersionDelegate(out uint version);

    public static GpuEncoderCapability Probe()
    {
        if (!OperatingSystem.IsWindows())
            return GpuEncoderCapability.Unavailable(BackendName, "NVENC is only probed on Windows.");

        if (!Environment.Is64BitProcess)
            return GpuEncoderCapability.Unavailable(BackendName, "NVENC direct path requires a 64-bit process.");

        if (!NativeLibrary.TryLoad(LibraryName, out var library))
            return GpuEncoderCapability.Unavailable(BackendName, $"{LibraryName} was not found.");

        try
        {
            if (!NativeLibrary.TryGetExport(library, VersionExportName, out var versionExport))
            {
                return GpuEncoderCapability.Unavailable(
                    BackendName,
                    $"{VersionExportName} export was not found.",
                    canOpenApi: true);
            }

            var getMaxSupportedVersion =
                Marshal.GetDelegateForFunctionPointer<NvEncodeApiGetMaxSupportedVersionDelegate>(versionExport);

            var status = getMaxSupportedVersion(out var version);
            if (status != 0)
            {
                return GpuEncoderCapability.Unavailable(
                    BackendName,
                    $"{VersionExportName} returned NVENC status {status}.",
                    canOpenApi: true);
            }

            var major = (int)(version & 0xFF);
            var minor = (int)((version >> 24) & 0xFF);
            return GpuEncoderCapability.Available(
                BackendName,
                $"{LibraryName} is loadable; driver reports NVENC API {major}.{minor}.",
                version,
                major,
                minor);
        }
        catch (Exception ex)
        {
            return GpuEncoderCapability.Unavailable(
                BackendName,
                $"NVENC probe failed: {ex.Message}",
                canOpenApi: true);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
