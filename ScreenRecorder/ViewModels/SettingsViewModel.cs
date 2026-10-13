using System.ComponentModel;
using System.Runtime.CompilerServices;
using ScreenRecorder.Models;
using ScreenRecorder.Services;

namespace ScreenRecorder.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _svc;

    public SettingsViewModel(SettingsService svc)
    {
        _svc = svc;

        // Yerel kopyaları ayarlardan oku
        _outputDir      = svc.Settings.OutputDirectory;
        _fps            = svc.Settings.Fps;
        _captureMode    = RecordingCaptureMode.Normalize(svc.Settings.CaptureMode);
        _captureTechnique = SothelCaptureEngine.Name;
        _quality        = svc.Settings.Quality;
        _resolution     = svc.Settings.OutputResolution;
        _micDeviceId    = svc.Settings.MicrophoneDeviceId;
        _sysAudio       = svc.Settings.SystemAudioEnabled;
        _hwAccel        = svc.Settings.HardwareAcceleration;
        _adaptiveScaling = svc.Settings.EnablePerformanceAdaptiveScaling;
        _hotkeyStart    = svc.Settings.HotkeyStartStop;
        _hotkeyPause    = svc.Settings.HotkeyPauseResume;
        _hotkeyMic      = svc.Settings.HotkeyMicToggle;
        _hotkeyShot     = svc.Settings.HotkeyScreenshot;

        // Mikrofon listesi
        var audioSvc = new AudioService();
        MicrophoneDevices = audioSvc.GetMicrophoneDevices();
        audioSvc.Dispose();

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        SaveCommand         = new RelayCommand(Save);
    }

    // ─── Özellikler ──────────────────────────────────────────────────────────

    private string _outputDir;
    public  string OutputDir { get => _outputDir; set { _outputDir = value; OnPropertyChanged(); } }

    private int _fps;

    private string _captureMode;
    public string CaptureMode
    {
        get => _captureMode;
        set
        {
            _captureMode = RecordingCaptureMode.Normalize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCaptureMode));
        }
    }

    public bool IsCaptureMode => RecordingCaptureMode.IsCapture(CaptureMode);

    private string _captureTechnique;
    public string CaptureTechnique
    {
        get => _captureTechnique;
        set
        {
            _captureTechnique = SothelCaptureEngine.Name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CaptureTechniqueDetail));
            OnPropertyChanged(nameof(CaptureTechniqueGate));
        }
    }

    public string CaptureTechniqueDetail =>
        SothelCaptureEngine.Summary;

    public string CaptureTechniqueGate =>
        SothelCaptureEngine.Gate;

    private string _quality;
    public  string Quality { get => _quality; set { _quality = value; OnPropertyChanged(); } }

    public  int Fps
    {
        get => _fps;
        set { _fps = value; OnPropertyChanged(); OnPropertyChanged(nameof(OutputPreview)); }
    }

    private string _resolution;
    public  string Resolution
    {
        get => _resolution;
        set { _resolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(OutputPreview)); }
    }

    /// <summary>
    /// Live preview of what the encoder will actually produce.
    /// Shows the real (source-aspect-correct) pixel size, not just the preset label.
    /// </summary>
    public string OutputPreview
    {
        get
        {
            var (sw, sh) = GetPrimaryScreenSize();
            var (ow, oh) = ComputeOutput(sw, sh, _resolution);
            return $"{ow}×{oh}  ·  {_fps} fps";
        }
    }

    private static (int w, int h) GetPrimaryScreenSize()
    {
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            using var adapter = factory.GetAdapter1(0);
            using var output  = adapter.GetOutput(0);
            var b = output.Description.DesktopBounds;
            return (b.Right - b.Left, b.Bottom - b.Top);
        }
        catch { return (1920, 1080); }
    }

    private static (int w, int h) ComputeOutput(int srcW, int srcH, string preset)
    {
        int targetH = preset switch
        {
            "1080p" => 1080,
            "1440p" => 1440,
            "4K"    => 2160,
            _       => 0
        };
        if (targetH == 0 || srcH <= 0 || targetH >= srcH) return (srcW, srcH);
        var h = targetH;
        var w = (int)Math.Round((double)srcW * targetH / srcH);
        if ((w & 1) == 1) w++;
        if ((h & 1) == 1) h++;
        return (w, h);
    }

    private string _micDeviceId;
    public  string MicDeviceId { get => _micDeviceId; set { _micDeviceId = value; OnPropertyChanged(); } }

    private bool _sysAudio;
    public  bool SysAudio { get => _sysAudio; set { _sysAudio = value; OnPropertyChanged(); } }

    private bool _hwAccel;
    public  bool HwAccel { get => _hwAccel; set { _hwAccel = value; OnPropertyChanged(); } }

    private bool _adaptiveScaling;
    public  bool AdaptiveScaling { get => _adaptiveScaling; set { _adaptiveScaling = value; OnPropertyChanged(); } }

    private string _hotkeyStart;
    public  string HotkeyStart { get => _hotkeyStart; set { _hotkeyStart = value; OnPropertyChanged(); } }

    private string _hotkeyPause;
    public  string HotkeyPause { get => _hotkeyPause; set { _hotkeyPause = value; OnPropertyChanged(); } }

    private string _hotkeyMic;
    public  string HotkeyMic   { get => _hotkeyMic;   set { _hotkeyMic   = value; OnPropertyChanged(); } }

    private string _hotkeyShot;
    public  string HotkeyScreenshot { get => _hotkeyShot; set { _hotkeyShot = value; OnPropertyChanged(); } }

    public List<AudioDevice> MicrophoneDevices { get; }

    public List<string> QualityOptions    { get; } = new() { "Düşük", "Orta", "Yüksek" };
    public List<int>    FpsOptions        { get; } = new() { 30, 60, 120 };
    public List<string> ResolutionOptions { get; } = new() { "Native", "1080p", "1440p", "4K" };
    public List<string> CaptureModeOptions { get; } = new() { RecordingCaptureMode.Normal, RecordingCaptureMode.Capture };
    public List<string> CaptureTechniqueOptions { get; } = new() { SothelCaptureEngine.Name };
    public List<string> CaptureTechniqueDescriptions { get; } = new()
    {
        SothelCaptureEngine.Summary,
        SothelCaptureEngine.Gate
    };

    // ─── Komutlar ─────────────────────────────────────────────────────────────
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand SaveCommand         { get; }

    public event Action? Saved;

    private void BrowseFolder()
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Kayıt klasörünü seçin",
            SelectedPath = OutputDir
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDir = dlg.SelectedPath;
    }

    private void Save()
    {
        var s = _svc.Settings;
        s.OutputDirectory   = OutputDir;
        s.Fps               = Fps;
        s.CaptureMode       = RecordingCaptureMode.Normalize(CaptureMode);
        s.CaptureTechnique  = SothelCaptureEngine.Name;
        s.Quality           = Quality;
        s.OutputResolution  = Resolution;
        s.MicrophoneDeviceId = MicDeviceId;
        s.SystemAudioEnabled = SysAudio;
        s.HardwareAcceleration = HwAccel;
        s.EnablePerformanceAdaptiveScaling = AdaptiveScaling;
        s.HotkeyStartStop   = HotkeyStart;
        s.HotkeyPauseResume = HotkeyPause;
        s.HotkeyMicToggle   = HotkeyMic;
        s.HotkeyScreenshot  = HotkeyScreenshot;
        _svc.Save();
        Saved?.Invoke();
    }

    // ─── INotifyPropertyChanged ───────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
