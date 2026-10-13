namespace ScreenRecorder.Models;

public static class RecordingCaptureMode
{
    public const string Normal = "Normal";
    public const string Capture = "Sothel Capture";

    public static string Normalize(string? value) =>
        string.Equals(value, Capture, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Capture", StringComparison.OrdinalIgnoreCase)
            ? Capture
            : Normal;

    public static bool IsCapture(string? value) =>
        string.Equals(Normalize(value), Capture, StringComparison.Ordinal);
}
