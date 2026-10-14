using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenRecorder.Services.Diagnostics;

public enum DiagnosticStage
{
    Startup,
    CaptureInit,
    GpuProbe,
    EncoderInit,
    EncodeFrame,
    MuxWrite,
    MuxFinish,
    AudioPipe,
    SegmentProbe,
    SegmentConcat,
    Screenshot,
    SelfTest
}

public sealed record DiagnosticEntry(
    DateTime Timestamp,
    DiagnosticStage Stage,
    string Summary,
    string? Detail);

public static class RecordingDiagnostics
{
    private static readonly object Sync = new();
    private static readonly List<DiagnosticEntry> RecentEntries = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Forecord",
        "logs");

    public static string LastErrorFilePath => Path.Combine(LogDirectory, "last_error.log");
    public static string SessionLogFilePath => Path.Combine(LogDirectory, "diagnostics.log");

    public static DiagnosticEntry? LastError { get; private set; }

    public static void Info(DiagnosticStage stage, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [{stage}] {message}";
        Debug.WriteLine(line);
        AppendLineSafe(SessionLogFilePath, line);
    }

    public static DiagnosticEntry ReportError(
        DiagnosticStage stage,
        string summary,
        Exception? exception = null,
        string? extraDetail = null)
    {
        var detailBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(extraDetail))
            detailBuilder.AppendLine(extraDetail.Trim());
        if (exception != null)
            detailBuilder.AppendLine(exception.ToString());

        var detail = detailBuilder.Length > 0 ? detailBuilder.ToString().TrimEnd() : null;
        var entry = new DiagnosticEntry(DateTime.Now, stage, summary, detail);

        lock (Sync)
        {
            LastError = entry;
            RecentEntries.Add(entry);
            if (RecentEntries.Count > 50)
                RecentEntries.RemoveAt(0);
        }

        var formatted = FormatEntry(entry);
        Debug.WriteLine(formatted);
        WriteAllTextSafe(LastErrorFilePath, formatted);
        AppendLineSafe(SessionLogFilePath, formatted);
        return entry;
    }

    public static IReadOnlyList<DiagnosticEntry> GetRecentErrors()
    {
        lock (Sync)
        {
            return RecentEntries.ToList();
        }
    }

    public static string FormatEntry(DiagnosticEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] [{entry.Stage}] {entry.Summary}");
        if (!string.IsNullOrWhiteSpace(entry.Detail))
            sb.AppendLine(entry.Detail);
        return sb.ToString().TrimEnd();
    }

    private static void WriteAllTextSafe(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void AppendLineSafe(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, content + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }
}
