namespace ScreenRecorder.Services.GpuEncoding;

public sealed record GpuEncoderCapability(
    string BackendName,
    bool IsAvailable,
    bool CanOpenApi,
    string Message,
    uint? MaxSupportedApiVersionRaw = null,
    int? MaxSupportedApiMajor = null,
    int? MaxSupportedApiMinor = null)
{
    public string ApiVersionText =>
        MaxSupportedApiMajor.HasValue && MaxSupportedApiMinor.HasValue
            ? $"{MaxSupportedApiMajor}.{MaxSupportedApiMinor}"
            : "unknown";

    public string ToDebugString()
    {
        var state = IsAvailable ? "available" : "unavailable";
        var version = MaxSupportedApiVersionRaw.HasValue
            ? $" api={ApiVersionText} raw=0x{MaxSupportedApiVersionRaw.Value:X}"
            : "";

        return $"[GPU encoder] {BackendName}: {state}; canOpenApi={CanOpenApi};{version} {Message}";
    }

    public static GpuEncoderCapability Unavailable(string backendName, string message, bool canOpenApi = false) =>
        new(backendName, false, canOpenApi, message);

    public static GpuEncoderCapability Available(
        string backendName,
        string message,
        uint maxSupportedApiVersionRaw,
        int maxSupportedApiMajor,
        int maxSupportedApiMinor) =>
        new(
            backendName,
            true,
            true,
            message,
            maxSupportedApiVersionRaw,
            maxSupportedApiMajor,
            maxSupportedApiMinor);
}
