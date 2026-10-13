using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WpfBrush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ScreenRecorder.Models;
using ScreenRecorder.ViewModels;

namespace ScreenRecorder.Views;

/// <summary>
/// Capsule-shaped floating toolbar that anchors to the recording floating
/// panel and exposes pen / eraser / color / thickness / undo / clear.
/// </summary>
public partial class DrawingToolbar : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private readonly DrawingToolViewModel _vm;
    private readonly DrawingOverlayWindow _overlay;
    private readonly Window _anchor;

    public DrawingToolbar(DrawingToolViewModel vm,
                          DrawingOverlayWindow overlay,
                          Window anchor)
    {
        InitializeComponent();
        _vm      = vm;
        _overlay = overlay;
        _anchor  = anchor;

        // Bind palette + thickness presets via brush list (Brush, not Color,
        // so the Ellipse Fill bindings work without a converter).
        ColorList.ItemsSource = DrawingToolViewModel.Palette
            .Select(c => (WpfBrush)new SolidColorBrush(c)).ToList();

        _vm.PropertyChanged += OnVmChanged;
        _overlay.HistoryChanged += OnOverlayHistoryChanged;
        _anchor.LocationChanged += (_, _) => Reposition();
        _anchor.SizeChanged     += (_, _) => Reposition();

        Loaded  += (_, _) =>
        {
            Reposition();
            // Hide toolbar from the recording — the floating panel does the
            // same via WDA_EXCLUDEFROMCAPTURE. The drawing overlay window is
            // intentionally NOT excluded so strokes appear in the video.
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
            catch { /* older Windows: silently continue */ }
        };
        Closed  += OnClosed;
        SyncToggleStates();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmChanged;
        _overlay.HistoryChanged -= OnOverlayHistoryChanged;
    }

    private void OnOverlayHistoryChanged() => Dispatcher.Invoke(SyncToggleStates);

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DrawingToolViewModel.Tool))
            Dispatcher.Invoke(SyncToggleStates);
    }

    private void SyncToggleStates()
    {
        PenBtn.IsChecked    = _vm.IsPen;
        EraserBtn.IsChecked = _vm.IsEraser;
        UndoBtn.IsEnabled   = _overlay.HasStrokes;
    }

    /// <summary>
    /// Anchor to the right edge of the floating panel; if not enough room,
    /// drop below.
    /// </summary>
    private void Reposition()
    {
        if (!IsLoaded) return;
        const int gap = 8;
        var screen = SystemParameters.WorkArea;
        // Try right of anchor.
        var x = _anchor.Left + _anchor.ActualWidth + gap;
        var y = _anchor.Top + (_anchor.ActualHeight - ActualHeight) / 2;
        if (x + ActualWidth > screen.Right - 4)
        {
            // Drop below anchor.
            x = _anchor.Left + (_anchor.ActualWidth - ActualWidth) / 2;
            y = _anchor.Top + _anchor.ActualHeight + gap;
        }
        Left = Math.Max(screen.Left + 4, x);
        Top  = Math.Max(screen.Top + 4,  y);
    }

    // ─── Tool actions ────────────────────────────────────────────────────────

    private void Pen_Click(object sender, RoutedEventArgs e)
    {
        _vm.Tool = _vm.IsPen ? DrawingTool.None : DrawingTool.Pen;
        SyncToggleStates();
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        _vm.Tool = _vm.IsEraser ? DrawingTool.None : DrawingTool.Eraser;
        SyncToggleStates();
    }

    private void ColorDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is SolidColorBrush b)
        {
            _vm.Color = b.Color;
            // Touching a color implicitly switches to pen — if eraser was
            // active, switch over so the chosen color is actually used.
            if (!_vm.IsPen) _vm.Tool = DrawingTool.Pen;
            SyncToggleStates();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _overlay.Undo();
        SyncToggleStates();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _overlay.ClearAll();
        SyncToggleStates();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Kullanıcı çağıran tarafa "kapat" sinyali verir; gerçek kapanış
        // owner tarafından (MainWindow) yönetilir, çünkü overlay ve toolbar
        // birlikte gösterilip gizlenmeli.
        CloseRequested?.Invoke();
    }

    public event Action? CloseRequested;
    public event Action<bool>? ThicknessToggleRequested;

    public bool IsThicknessOpen
    {
        get => ThicknessBtn.IsChecked == true;
        set => ThicknessBtn.IsChecked = value;
    }

    private void ThicknessToggle_Click(object sender, RoutedEventArgs e)
    {
        ThicknessToggleRequested?.Invoke(ThicknessBtn.IsChecked == true);
    }
}
