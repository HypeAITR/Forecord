namespace ScreenRecorder.Services.GpuEncoding;

internal sealed class NvencApiException : InvalidOperationException
{
    public NvencApiException(string apiName, int status, string statusName, string? detail = null)
        : base(BuildMessage(apiName, status, statusName, detail))
    {
        ApiName = apiName;
        Status = status;
        StatusName = statusName;
    }

    public string ApiName { get; }
    public int Status { get; }
    public string StatusName { get; }

    private static string BuildMessage(string apiName, int status, string statusName, string? detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $" {detail.Trim()}";
        return $"{apiName} failed: {statusName} ({status}).{suffix}";
    }
}
