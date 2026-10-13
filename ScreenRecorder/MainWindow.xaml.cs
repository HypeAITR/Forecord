using System.Windows;
using System.Windows.Input;
using ScreenRecorder.Models;
using ScreenRecorder.ViewModels;
using ScreenRecorder.Views;

namespace ScreenRecorder;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private FloatingPanel?         _floatingPanel;
    private DrawingOverlayWindow?  _drawingOverlay;
    private DrawingToolbar?        _drawingToolbar;
    private DrawingThicknessPanel? _drawingThickness;
    private readonly DrawingToolViewModel _drawingVm = new();

    public MainWindow()
    {
        InitializeComponent();
        _vm          = App.MainVM;
        DataContext  = _vm;

        // Hotkey servisi bu pencereye bağlı
        App.HotkeySvc.Initialize(this);
        _vm.BindHotkeys();

        // Olayları bağla
        _vm.RequestShowFloating  += ShowFloating;
        _vm.RequestHideFloating  += HideFloating;
        _vm.RecordingCompleted   += OnRecordingCompleted;
        _vm.ScreenshotSaved      += OnScreenshotSaved;
        _vm.RequestOpenSettings  += OpenSettings;
        _vm.AnnotationVisibilityChanged += OnAnnotationVisibilityChanged;
    }

    // ─── Yüzen panel ─────────────────────────────────────────────────────────

    private void ShowFloating(Window _)
    {
        // If the panel was closed (e.g. via Alt+F4), we must create a new one.
        // A WPF Window cannot be Show()n again after it has been closed.
        if (_floatingPanel == null || !_floatingPanel.IsLoaded)
        {
            _floatingPanel = new FloatingPanel(_vm);
        }
        _floatingPanel.Show();
        // Ana pencereyi küçült – kayıt başladığında ekranda görünmez
        WindowState = WindowState.Minimized;
    }

    private void HideFloating()
    {
        if (_floatingPanel != null)
        {
            try { _floatingPanel.Close(); } catch { }
            _floatingPanel = null;
        }
        // Ana pencere küçültülmüş kalır — kullanıcı taskbar'dan tıklayana
        // (veya toast'taki butonlardan birine basana) kadar öne çıkmaz.
    }

    // ─── Kayıt tamamlandı ────────────────────────────────────────────────────

    private void OnRecordingCompleted(string path)
    {
        // Toast bildirimi — modal değil, ekranın sağ üstünde belirir
        var toast = new RecordingCompleteDialog(path, RecordingCompleteDialog.Kind.Recording);
        toast.Show();
    }

    private void OnScreenshotSaved(string path)
    {
        var toast = new RecordingCompleteDialog(path, RecordingCompleteDialog.Kind.Screenshot);
        toast.Show();
    }

    // ─── Çizim katmanı ───────────────────────────────────────────────────────
    private void OnAnnotationVisibilityChanged(bool open)
    {
        if (open) ShowAnnotation();
        else      HideAnnotation();
    }

    private void ShowAnnotation()
    {
        if (_floatingPanel == null) return;     // recording not active

        if (_drawingOverlay == null || !_drawingOverlay.IsLoaded)
        {
            _drawingOverlay = new DrawingOverlayWindow(_drawingVm);
            _drawingOverlay.Show();
        }
        _drawingOverlay.RegisterExcludedWindow(_floatingPanel);

        if (_drawingToolbar == null || !_drawingToolbar.IsLoaded)
        {
            _drawingToolbar = new DrawingToolbar(_drawingVm, _drawingOverlay, _floatingPanel);
            _drawingToolbar.CloseRequested += () => _vm.IsAnnotationOpen = false;
            _drawingToolbar.ThicknessToggleRequested += OnThicknessToggleRequested;
            _drawingToolbar.Show();
        }
        _drawingOverlay.RegisterExcludedWindow(_drawingToolbar);

        // Default to pen on first open so the user can draw immediately.
        if (_drawingVm.Tool == DrawingTool.None)
            _drawingVm.Tool = DrawingTool.Pen;

        // Hotkeys for the drawing session — consumed at app level via the
        // floating panel which owns keyboard focus when clicked. Use a global
        // hook on the toolbar window for D / E / Esc / Ctrl+Z / C.
        _drawingToolbar.PreviewKeyDown -= DrawingHotkeys;
        _drawingToolbar.PreviewKeyDown += DrawingHotkeys;
        _drawingOverlay.PreviewKeyDown -= DrawingHotkeys;
        _drawingOverlay.PreviewKeyDown += DrawingHotkeys;
    }

    private void OnThicknessToggleRequested(bool open)
    {
        if (_drawingToolbar == null) return;
        if (open)
        {
            if (_drawingThickness == null || !_drawingThickness.IsLoaded)
            {
                _drawingThickness = new DrawingThicknessPanel(_drawingVm, _drawingToolbar);
                _drawingThickness.Closed += (_, _) =>
                {
                    if (_drawingToolbar != null) _drawingToolbar.IsThicknessOpen = false;
                };
                _drawingThickness.Show();
            }
            _drawingOverlay?.RegisterExcludedWindow(_drawingThickness);
        }
        else
        {
            if (_drawingThickness != null)
                _drawingOverlay?.UnregisterExcludedWindow(_drawingThickness);
            try { _drawingThickness?.Close(); } catch { }
            _drawingThickness = null;
        }
    }

    private void HideAnnotation()
    {
        try
        {
            _drawingOverlay?.ClearAll();
            _drawingOverlay?.Close();
            _drawingToolbar?.Close();
            _drawingThickness?.Close();
        }
        catch { /* swallow — windows may already be closing */ }
        _drawingOverlay   = null;
        _drawingToolbar   = null;
        _drawingThickness = null;
        _drawingVm.Tool   = DrawingTool.None;
    }

    private void DrawingHotkeys(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D when Keyboard.Modifiers == ModifierKeys.None:
                _drawingVm.Tool = DrawingTool.Pen; e.Handled = true; break;
            case Key.E when Keyboard.Modifiers == ModifierKeys.None:
                _drawingVm.Tool = DrawingTool.Eraser; e.Handled = true; break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                _drawingOverlay?.Undo(); e.Handled = true; break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.None:
                _drawingOverlay?.ClearAll(); e.Handled = true; break;
            case Key.Escape:
                _drawingVm.Tool = DrawingTool.None; e.Handled = true; break;
        }
    }

    // ─── Ayarlar ─────────────────────────────────────────────────────────────

    private void OpenSettings()
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.RebindHotkeys();
    }

    // ─── Başlık çubuğu ───────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CanClose())
            System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.IsIdle)
        {
            e.Cancel = true;
            _vm.CanClose(); // uyarı göster
        }
    }
}
