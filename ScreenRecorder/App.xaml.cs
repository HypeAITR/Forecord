using ScreenRecorder.Services;
using ScreenRecorder.Services.Diagnostics;
using ScreenRecorder.ViewModels;

namespace ScreenRecorder;

public partial class App : System.Windows.Application
{
    // Tek örnek servisler – tüm uygulama ömrü boyunca yaşar
    public static SettingsService  SettingsSvc  { get; } = new();
    public static EncoderService   EncoderSvc   { get; } = new();
    public static AudioService     AudioSvc     { get; } = new();
    public static FileService      FileSvc      { get; } = new();
    public static HotkeyService    HotkeySvc    { get; } = new();

    public static RecordingService RecordingSvc { get; private set; } = null!;
    public static MainViewModel    MainVM        { get; private set; } = null!;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            var report = PipelineSelfTest.RunAll();
            Environment.Exit(report.AllPassed ? 0 : 1);
            return;
        }

        base.OnStartup(e);

        SettingsSvc.Load();
        EncoderSvc.FindFFmpeg();

        // Donanım encoder tespitini arka planda yap (3 sn'lik freeze'i önler)
        _ = EncoderSvc.PrewarmEncoderDetectionAsync(SettingsSvc.Settings.HardwareAcceleration);

        RecordingSvc = new RecordingService(EncoderSvc, AudioSvc, FileSvc);
        MainVM       = new MainViewModel(SettingsSvc, RecordingSvc, FileSvc, HotkeySvc);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { HotkeySvc.Dispose(); } catch { }
        try { RecordingSvc.Dispose(); } catch { }
        try { AudioSvc.Dispose(); } catch { }
        base.OnExit(e);
    }
}
