using System.Diagnostics;
using System.IO;

namespace ScreenRecorder.Services;

public class FileService
{
    public string GenerateOutputPath(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return Path.Combine(outputDirectory, $"Kayit_{ts}.mp4");
    }

    public string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Forecord",
                               Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void OpenFile(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            { UseShellExecute = true });
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            { UseShellExecute = true });
    }

    public void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
        { UseShellExecute = true });
    }

    public void CleanupTemp(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { }
    }
}
