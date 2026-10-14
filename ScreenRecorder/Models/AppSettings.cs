using System.IO;

namespace ScreenRecorder.Models;

public class AppSettings
{
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kayıtlar");

    public int Fps { get; set; } = 60;

    // "Düşük" | "Orta" | "Yüksek"
    public string Quality { get; set; } = "Yüksek";

    // "Native" | "1080p" | "1440p" | "4K"
    public string OutputResolution { get; set; } = "Native";

    public bool MicrophoneEnabled { get; set; } = false;
    public bool SystemAudioEnabled { get; set; } = false;

    // Boş string → varsayılan mikrofon
    public string MicrophoneDeviceId { get; set; } = "";

    public bool HardwareAcceleration { get; set; } = true;

    public bool EnablePerformanceAdaptiveScaling { get; set; } = true;

    public bool EnableGpuDirectPipeline { get; set; } = true;

    public int MonitorIndex { get; set; } = 0;

    public string HotkeyStartStop { get; set; } = "Ctrl+Alt+R";
    public string HotkeyPauseResume { get; set; } = "Ctrl+Alt+P";
    public string HotkeyMicToggle { get; set; } = "Ctrl+Alt+M";
    public string HotkeyScreenshot { get; set; } = "Ctrl+Alt+S";
}
