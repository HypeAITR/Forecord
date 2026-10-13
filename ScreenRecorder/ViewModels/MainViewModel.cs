using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ScreenRecorder.Models;
using ScreenRecorder.Services;

namespace ScreenRecorder.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    // ─── Servisler ───────────────────────────────────────────────────────────
    public  readonly SettingsService Settings;
    private readonly RecordingService _rec;
    private readonly FileService      _files;
    private readonly HotkeyService    _hotkeys;

    // ─── Timer ───────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _uiTimer  = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch       _stopwatch = new();

    // ─── Durum ───────────────────────────────────────────────────────────────
    private RecordingState _state = RecordingState.Idle;
    public  RecordingState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPaused));
            StartStopCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
            DiscardCommand.RaiseCanExecuteChanged();
            ScreenshotCommand.RaiseCanExecuteChanged();
            ToggleAnnotationCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle       => State == RecordingState.Idle;
    public bool IsRecording  => State == RecordingState.Recording;
    public bool IsPaused     => State == RecordingState.Paused;

    // True while ffmpeg is still flushing / concat-encoding the last recording
    // in the background. UI returns to "Idle" the moment the user clicks stop,
    // but a new recording cannot begin until the previous one's segment lists
    // and temp dir have been finalized — RecordingService re-uses those fields.
    private bool _finalizing;
    public  bool IsFinalizing
    {
        get => _finalizing;
        private set
        {
            if (_finalizing == value) return;
            _finalizing = value;
            OnPropertyChanged();
            StartStopCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
            DiscardCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isSegmentTransitioning;
    public bool IsSegmentTransitioning
    {
        get => _isSegmentTransitioning;
        private set
        {
            if (_isSegmentTransitioning == value) return;
            _isSegmentTransitioning = value;
            OnPropertyChanged();
            StartStopCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
            DiscardCommand.RaiseCanExecuteChanged();
            ToggleMicCommand.RaiseCanExecuteChanged();
            ToggleSysCommand.RaiseCanExecuteChanged();
        }
    }

    // ─── Bağlı özellikler ────────────────────────────────────────────────────
    private string _timerText = "00:00:00";
    public  string TimerText  { get => _timerText; private set { _timerText = value; OnPropertyChanged(); } }

    private bool _micEnabled;
    public  bool MicEnabled
    {
        get => _micEnabled;
        set
        {
            if (_micEnabled == value) return;
            _micEnabled = value;
            Settings.Settings.MicrophoneEnabled = value;
            OnPropertyChanged();
            RestartSegmentIfRecording();
        }
    }

    private bool _sysAudioEnabled;
    public  bool SysAudioEnabled
    {
        get => _sysAudioEnabled;
        set
        {
            if (_sysAudioEnabled == value) return;
            _sysAudioEnabled = value;
            Settings.Settings.SystemAudioEnabled = value;
            OnPropertyChanged();
            RestartSegmentIfRecording();
        }
    }

    // Floating panel'den mikrofon / sistem sesi açılınca aktif segment yeni
    // ayarla yeniden başlasın; aksi halde değişiklik bir sonraki segmente
    // (pause/resume sonrası) kadar uygulanmaz ve kullanıcı "ses gelmiyor" der.
    private void RestartSegmentIfRecording()
    {
        if (!IsRecording) return;
        _ = Task.Run(async () =>
        {
            try { await _rec.RestartSegmentAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[RestartSegment] {ex.Message}"); }
        });
    }

    private string _statusMsg = "Kayda hazır";
    public  string StatusMsg  { get => _statusMsg; set { _statusMsg = value; OnPropertyChanged(); } }

    private bool _isAnnotationOpen;
    public  bool IsAnnotationOpen
    {
        get => _isAnnotationOpen;
        set
        {
            if (_isAnnotationOpen == value) return;
            _isAnnotationOpen = value;
            OnPropertyChanged();
            AnnotationVisibilityChanged?.Invoke(value);
        }
    }
    public event Action<bool>? AnnotationVisibilityChanged;

    // ─── Komutlar ────────────────────────────────────────────────────────────
    public RelayCommand StartStopCommand   { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand DiscardCommand     { get; }
    public RelayCommand ScreenshotCommand  { get; }
    public RelayCommand ToggleAnnotationCommand { get; }
    public RelayCommand ToggleMicCommand   { get; }
    public RelayCommand ToggleSysCommand   { get; }
    public RelayCommand OpenFolderCommand  { get; }
    public RelayCommand OpenSettingsCommand { get; }

    // ─── Olaylar ─────────────────────────────────────────────────────────────
    public event Action<Window>? RequestShowFloating;
    public event Action?         RequestHideFloating;
    public event Action<string>? RecordingCompleted;
    public event Action<string>? ScreenshotSaved;
    public event Action?         RequestOpenSettings;

    // ─── Oluşturucu ──────────────────────────────────────────────────────────
    public MainViewModel(
        SettingsService settings,
        RecordingService rec,
        FileService files,
        HotkeyService hotkeys)
    {
        Settings = settings;
        _rec     = rec;
        _files   = files;
        _hotkeys = hotkeys;

        _micEnabled      = settings.Settings.MicrophoneEnabled;
        _sysAudioEnabled = settings.Settings.SystemAudioEnabled;

        StartStopCommand    = new RelayCommand(OnStartStop, () => !IsFinalizing);
        PauseResumeCommand  = new RelayCommand(OnPauseResume,  () => !IsIdle && !IsFinalizing);
        DiscardCommand      = new RelayCommand(OnDiscard,      () => !IsIdle && !IsFinalizing && !IsSegmentTransitioning);
        ScreenshotCommand   = new RelayCommand(OnScreenshot,   () => !IsIdle);
        ToggleAnnotationCommand = new RelayCommand(() =>
        {
            IsAnnotationOpen = !IsAnnotationOpen;
        }, () => !IsIdle);
        ToggleMicCommand    = new RelayCommand(() => MicEnabled      = !MicEnabled,      () => !IsSegmentTransitioning);
        ToggleSysCommand    = new RelayCommand(() => SysAudioEnabled = !SysAudioEnabled, () => !IsSegmentTransitioning);
        OpenFolderCommand   = new RelayCommand(() => _files.OpenDirectory(settings.Settings.OutputDirectory));
        OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());

        _uiTimer.Tick += (_, _) =>
        {
            if (State == RecordingState.Recording)
                TimerText = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        };
    }

    // ─── Kısayol tuşları bağlantısı ──────────────────────────────────────────
    public void BindHotkeys()
    {
        _hotkeys.Register(Settings.Settings.HotkeyStartStop,   OnStartStop);
        _hotkeys.Register(Settings.Settings.HotkeyPauseResume, OnPauseResume);
        _hotkeys.Register(Settings.Settings.HotkeyMicToggle,   () => MicEnabled = !MicEnabled);
        _hotkeys.Register(Settings.Settings.HotkeyScreenshot,  OnScreenshot);
    }

    public void RebindHotkeys()
    {
        _hotkeys.UnregisterAll();
        BindHotkeys();
    }

    // ─── Komut işleyiciler ────────────────────────────────────────────────────

    private async void OnStartStop()
    {
        if (IsFinalizing) return;  // hotkey path bypasses CanExecute
        if (IsIdle)
        {
            if (IsSegmentTransitioning) return;
            await StartRecordingAsync();
        }
        else
        {
            await StopRecordingAsync();
        }
    }

    private async void OnPauseResume()
    {
        if (IsFinalizing || IsSegmentTransitioning) return;
        if (IsRecording) await PauseAsync();
        else if (IsPaused) await ResumeAsync();
    }

    private async Task StartRecordingAsync()
    {
        StatusMsg = "Kayıt başlatılıyor…";
        try
        {
            await _rec.StartAsync(Settings.Settings);
            State     = RecordingState.Recording;
            StatusMsg = "Kayıt devam ediyor";
            _stopwatch.Restart();
            _uiTimer.Start();
            RequestShowFloating?.Invoke(null!);
        }
        catch (Exception ex)
        {
            State     = RecordingState.Idle;
            StatusMsg = "Hata – " + ex.Message;
            System.Windows.MessageBox.Show(ex.Message, "Kayıt Hatası", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task PauseAsync()
    {
        if (IsSegmentTransitioning) return;

        IsSegmentTransitioning = true;
        _stopwatch.Stop();
        State     = RecordingState.Paused;
        StatusMsg = "Duraklatılıyor…";

        try
        {
            await Task.Run(_rec.Pause);
            if (IsPaused && !IsFinalizing)
                StatusMsg = "Duraklatıldı";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Pause] {ex}");
            if (!IsIdle && !IsFinalizing)
            {
                State = RecordingState.Recording;
                _stopwatch.Start();
                StatusMsg = "Hata – " + ex.Message;
            }
        }
        finally
        {
            IsSegmentTransitioning = false;
        }
    }

    private async Task ResumeAsync()
    {
        if (IsSegmentTransitioning) return;

        IsSegmentTransitioning = true;
        StatusMsg = "Devam ediliyor…";

        try
        {
            await _rec.ResumeAsync();
            if (IsIdle || IsFinalizing) return;
            _stopwatch.Start();
            State     = RecordingState.Recording;
            StatusMsg = "Kayıt devam ediyor";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Resume] {ex}");
            if (!IsIdle && !IsFinalizing)
                StatusMsg = "Hata – " + ex.Message;
        }
        finally
        {
            IsSegmentTransitioning = false;
        }
    }

    private async void OnScreenshot()
    {
        if (IsIdle) return;
        try
        {
            var dir  = Settings.Settings.OutputDirectory;
            System.IO.Directory.CreateDirectory(dir);
            var name = $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var path = System.IO.Path.Combine(dir, name);
            if (await _rec.TrySaveScreenshotAsync(path))
            {
                StatusMsg = "Ekran görüntüsü kaydedildi";
                ScreenshotSaved?.Invoke(path);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Screenshot] {ex.Message}"); }
    }

    private async void OnDiscard()
    {
        if (IsIdle || IsFinalizing || IsSegmentTransitioning) return;

        var confirmed = ScreenRecorder.Views.ConfirmDialog.Show(
            owner: null,
            title:   "Kaydı sil",
            message: "Mevcut kayıt iptal edilip dosyaları silinecek. Devam edilsin mi?",
            confirmText: "Sil",
            cancelText:  "Vazgeç",
            destructive: true);
        if (!confirmed) return;

        _uiTimer.Stop();
        _stopwatch.Stop();
        TimerText = "00:00:00";
        IsSegmentTransitioning = true;
        State     = RecordingState.Idle;
        IsAnnotationOpen = false;
        RequestHideFloating?.Invoke();
        StatusMsg = "Kayıt siliniyor…";

        try { await _rec.DiscardAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[Discard] {ex.Message}"); }
        finally { IsSegmentTransitioning = false; }

        State     = RecordingState.Idle;
        StatusMsg = "Kayda hazır";
        IsAnnotationOpen = false;
        RequestHideFloating?.Invoke();
    }

    // The actual background work for the most recent stop. CanClose() awaits
    // this so the app doesn't shut down mid-flush.
    private Task _finalizeTask = Task.CompletedTask;

    private Task StopRecordingAsync()
    {
        // Snap UI to Idle right away — the user pressed stop and shouldn't
        // wait for ffmpeg to flush + concat + AAC-encode (5–15 s on a 30-min
        // capture). The floating panel disappears, the timer resets, and the
        // main window returns. Finalization continues on a background thread;
        // when it lands, the Recording-Complete toast fires.
        _uiTimer.Stop();
        _stopwatch.Stop();
        TimerText  = "00:00:00";
        IsSegmentTransitioning = false;
        State      = RecordingState.Idle;
        StatusMsg  = "Dosya hazırlanıyor…";
        IsFinalizing = true;
        IsAnnotationOpen = false;
        RequestHideFloating?.Invoke();

        _finalizeTask = Task.Run(async () =>
        {
            string? path = null;
            string? error = null;
            try
            {
                // Hard cap so the floating panel can never stay on "Dosya
                // hazırlanıyor…" forever: ffmpeg / NAudio dispose has its own
                // internal timeouts, but if both stack up worst-case (8s + 10s
                // + 2s + ffmpeg mux up to 20m + buffer) we still want a hard
                // ceiling that surfaces the error instead of hanging the UI.
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(22));
                path = await _rec.StopAsync().WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                error = "Sonlandırma zaman aşımına uğradı.";
                Debug.WriteLine("[Stop] Finalization timed out.");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[Stop] {ex}");
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsFinalizing = false;
                if (path != null)
                {
                    StatusMsg = "Kayda hazır";
                    RecordingCompleted?.Invoke(path);
                }
                else
                {
                    StatusMsg = "Hata – " + (error ?? "Bilinmeyen");
                    System.Windows.MessageBox.Show(error ?? "Bilinmeyen hata",
                        "Dışa Aktarma Hatası",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            });
        });

        return Task.CompletedTask;
    }

    // Uygulama kapatılmadan önce kontrol
    public bool CanClose()
    {
        if (IsIdle) return true;

        var r = System.Windows.MessageBox.Show(
            "Kayıt devam ediyor. Durdurup çıkmak ister misiniz?",
            "Çıkış Onayı",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

        if (r == System.Windows.MessageBoxResult.Yes)
        {
            // Kick off stop, then wait on the actual background finalize task
            // (StopRecordingAsync now returns instantly for snappy UX).
            _ = StopRecordingAsync().ContinueWith(_ => _finalizeTask)
                .Unwrap()
                .ContinueWith(_ => System.Windows.Application.Current.Dispatcher.Invoke(
                                       System.Windows.Application.Current.Shutdown),
                              System.Threading.Tasks.TaskScheduler.Default);
            return false;
        }
        return false;
    }

    // ─── INotifyPropertyChanged ───────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
