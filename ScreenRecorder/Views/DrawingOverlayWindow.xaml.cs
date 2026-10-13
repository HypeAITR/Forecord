using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenRecorder.Models;
using ScreenRecorder.ViewModels;

namespace ScreenRecorder.Views;

/// <summary>
/// Fullscreen transparent topmost window that hosts an <see cref="InkCanvas"/>
/// for live annotation. When no tool is active the window is click-through
/// (WS_EX_TRANSPARENT) so the user can keep working with the desktop while
/// previously-drawn strokes remain visible.
/// </summary>
public partial class DrawingOverlayWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1,int y1,int x2,int y2);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int  CombineRgn(IntPtr d, IntPtr s1, IntPtr s2, int mode);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr r, bool redraw);

    private const int GWL_EXSTYLE        = -20;
    private const int WS_EX_TRANSPARENT  = 0x00000020;
    private const int RGN_DIFF           = 4;
    private const double MinArrowLength  = 4.0;

    // Mouse'u geçirteceğimiz pencereler (toolbar / thickness panel / floating
    // panel). Overlay HRGN'sinden bu pencerelerin rectangle'ları DIFF ile
    // çıkarılır; Windows seviyesinde tıklama overlay'e değil onlara gider.
    private readonly List<Window> _excluded = new();
    private readonly Dictionary<Window, ExclusionHandlers> _exclusionHandlers = new();

    private readonly DrawingToolViewModel _vm;
    private IntPtr _hwnd;

    // Undo geçmişi: hem stroke hem arrow için ortak. Pop edilen action
    // kendisini geri alır (kaldırır).
    private readonly Stack<Action> _undoHistory = new();
    private bool _suppressHistory;

    // Arrow çizim durumu
    private bool _arrowDragging;
    private System.Windows.Point _arrowStart;
    private Path? _arrowPath;

    public DrawingOverlayWindow(DrawingToolViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _vm.PropertyChanged += OnVmChanged;

        // Cover the virtual desktop.
        var s = SystemParameters.WorkArea;
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += OnLoaded;
        Closed += OnClosed;
        ApplyToolToInkCanvas();

        // Stroke listener — pen ile çizilen her stroke için undo lambda'sı.
        Ink.Strokes.StrokesChanged += OnInkStrokesChanged;

        // Arrow modu için preview-level mouse handler. EditingMode=None iken
        // InkCanvas freehand yapmaz, biz kendi ok geometrimizi çizeriz.
        Ink.PreviewMouseLeftButtonDown += Ink_OnArrowDown;
        Ink.PreviewMouseMove           += Ink_OnArrowMove;
        Ink.PreviewMouseLeftButtonUp   += Ink_OnArrowUp;
        Ink.PreviewMouseLeftButtonDown += Ink_OnEraserDown;
        Ink.PreviewMouseMove           += Ink_OnEraserMove;
    }

    private void OnInkStrokesChanged(object? sender, StrokeCollectionChangedEventArgs args)
    {
        if (_suppressHistory) return;

        foreach (var s in args.Added)
        {
            var stroke = s;
            PushUndo(() =>
            {
                _suppressHistory = true;
                try
                {
                    if (Ink.Strokes.Contains(stroke)) Ink.Strokes.Remove(stroke);
                }
                finally
                {
                    _suppressHistory = false;
                }
            });
        }

        if (_vm.IsEraser && args.Removed.Count > 0)
        {
            var removed = args.Removed.ToList();
            PushUndo(() =>
            {
                _suppressHistory = true;
                try
                {
                    foreach (var stroke in removed)
                    {
                        if (!Ink.Strokes.Contains(stroke)) Ink.Strokes.Add(stroke);
                    }
                }
                finally
                {
                    _suppressHistory = false;
                }
            });
        }
    }

    private void Ink_OnArrowDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Tool != DrawingTool.Arrow) return;
        RemoveCurrentArrowPreview();
        _arrowDragging = true;
        _arrowStart = e.GetPosition(Ink);
        Ink.CaptureMouse();
        e.Handled = true;
    }

    private void Ink_OnArrowMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_arrowDragging) return;
        var p = e.GetPosition(Ink);
        if (_arrowPath == null && Distance(_arrowStart, p) < MinArrowLength) return;

        _arrowPath ??= CreateArrowPath(_arrowStart, _arrowStart);
        if (!Ink.Children.Contains(_arrowPath)) Ink.Children.Add(_arrowPath);
        _arrowPath.Data = BuildArrowGeometry(_arrowStart, p, _vm.PenThickness);
    }

    private void Ink_OnArrowUp(object sender, MouseButtonEventArgs e)
    {
        if (!_arrowDragging) return;
        _arrowDragging = false;
        Ink.ReleaseMouseCapture();
        var pathRef = _arrowPath;
        _arrowPath = null;
        var end = e.GetPosition(Ink);
        if (pathRef != null && Distance(_arrowStart, end) >= MinArrowLength)
        {
            PushUndo(() =>
            {
                if (Ink.Children.Contains(pathRef)) Ink.Children.Remove(pathRef);
            });
        }
        else if (pathRef != null)
        {
            Ink.Children.Remove(pathRef);
        }
        e.Handled = true;
    }

    private Path CreateArrowPath(System.Windows.Point start, System.Windows.Point end) => new()
    {
        Stroke = new SolidColorBrush(_vm.Color),
        StrokeThickness = Math.Max(2, _vm.PenThickness),
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap   = PenLineCap.Round,
        StrokeLineJoin     = PenLineJoin.Round,
        Data = BuildArrowGeometry(start, end, _vm.PenThickness),
        IsHitTestVisible = false,
    };

    private void RemoveCurrentArrowPreview()
    {
        if (_arrowPath != null)
            Ink.Children.Remove(_arrowPath);

        _arrowDragging = false;
        _arrowPath = null;
        if (Ink.IsMouseCaptured) Ink.ReleaseMouseCapture();
    }

    private void Ink_OnEraserDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Tool != DrawingTool.Eraser) return;
        if (EraseArrowAt(e.GetPosition(Ink))) e.Handled = true;
    }

    private void Ink_OnEraserMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_vm.Tool != DrawingTool.Eraser || e.LeftButton != MouseButtonState.Pressed) return;
        EraseArrowAt(e.GetPosition(Ink));
    }

    private bool EraseArrowAt(System.Windows.Point point)
    {
        foreach (var path in Ink.Children.OfType<Path>().ToArray())
        {
            var strokeThickness = path.StrokeThickness > 0 ? path.StrokeThickness : _vm.PenThickness;
            var hitThickness = Math.Max(_vm.EraserThickness, strokeThickness + _vm.EraserThickness * 0.5);
            if (path.Data?.StrokeContains(new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, hitThickness), point) != true)
                continue;

            var index = Ink.Children.IndexOf(path);
            Ink.Children.Remove(path);
            PushUndo(() =>
            {
                if (Ink.Children.Contains(path)) return;
                var insertAt = Math.Clamp(index, 0, Ink.Children.Count);
                Ink.Children.Insert(insertAt, path);
            });
            return true;
        }

        return false;
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Geometry BuildArrowGeometry(System.Windows.Point start,
                                               System.Windows.Point end,
                                               double thickness)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) len = 1;

        // Arrowhead boyutu kalınlığa orantılı, min 10px.
        var head = Math.Max(10, thickness * 3);
        var ang = Math.Atan2(dy, dx);
        const double arrowDeg = 28.0;
        var a1 = ang + Math.PI - arrowDeg * Math.PI / 180.0;
        var a2 = ang + Math.PI + arrowDeg * Math.PI / 180.0;
        var p1 = new System.Windows.Point(end.X + Math.Cos(a1) * head,
                                          end.Y + Math.Sin(a1) * head);
        var p2 = new System.Windows.Point(end.X + Math.Cos(a2) * head,
                                          end.Y + Math.Sin(a2) * head);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.LineTo(end, isStroked: true, isSmoothJoin: true);
            ctx.BeginFigure(p1,    isFilled: false, isClosed: false);
            ctx.LineTo(end, isStroked: true, isSmoothJoin: true);
            ctx.LineTo(p2,  isStroked: true, isSmoothJoin: true);
        }
        geo.Freeze();
        return geo;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Click-through is the ONLY ex-style we toggle. Flags like
        // WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW interfere with WPF's mouse
        // capture path and stop InkCanvas from receiving stroke events.
        UpdateClickThrough();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnVmChanged;
        Ink.Strokes.StrokesChanged -= OnInkStrokesChanged;
        foreach (var window in _excluded.ToArray())
            UnregisterExcludedWindow(window);

        if (_hwnd != IntPtr.Zero)
            SetWindowRgn(_hwnd, IntPtr.Zero, true);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DrawingToolViewModel.Tool))
        {
            ApplyToolToInkCanvas();
            UpdateClickThrough();
        }
        else if (e.PropertyName is nameof(DrawingToolViewModel.Color)
                                or nameof(DrawingToolViewModel.PenThickness)
                                or nameof(DrawingToolViewModel.EraserThickness))
        {
            ApplyToolToInkCanvas();
        }
    }

    private void ApplyToolToInkCanvas()
    {
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color           = _vm.Color,
            Width           = _vm.PenThickness,
            Height          = _vm.PenThickness,
            FitToCurve      = true,
            IgnorePressure  = true,
            StylusTip       = StylusTip.Ellipse,
            IsHighlighter   = false,
        };
        var eraserSize = Math.Max(_vm.EraserThickness, 4);
        Ink.EraserShape = new System.Windows.Ink.EllipseStylusShape(eraserSize, eraserSize);

        Ink.EditingMode = _vm.Tool switch
        {
            DrawingTool.Pen     => InkCanvasEditingMode.Ink,
            DrawingTool.Eraser  => InkCanvasEditingMode.EraseByStroke,
            _                   => InkCanvasEditingMode.None,  // Arrow + None
        };
    }

    private void UpdateClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (_vm.IsActive)
            ex &= ~WS_EX_TRANSPARENT;   // capture mouse → can draw / erase
        else
            ex |=  WS_EX_TRANSPARENT;   // pass mouse through to desktop apps
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        UpdateWindowRegion();
    }

    public void RegisterExcludedWindow(Window window)
    {
        if (_excluded.Contains(window)) return;

        _excluded.Add(window);

        RoutedEventHandler loadedUpdate = (_, _) => QueueRegionUpdate();
        EventHandler locationUpdate = (_, _) => QueueRegionUpdate();
        SizeChangedEventHandler sizeUpdate = (_, _) => QueueRegionUpdate();
        DependencyPropertyChangedEventHandler visibilityUpdate = (_, _) => QueueRegionUpdate();
        EventHandler closed = (_, _) => UnregisterExcludedWindow(window);

        window.Loaded += loadedUpdate;
        window.LocationChanged += locationUpdate;
        window.SizeChanged += sizeUpdate;
        window.IsVisibleChanged += visibilityUpdate;
        window.Closed += closed;

        _exclusionHandlers[window] = new ExclusionHandlers(loadedUpdate, locationUpdate, sizeUpdate, visibilityUpdate, closed);
        QueueRegionUpdate();
    }

    public void UnregisterExcludedWindow(Window window)
    {
        if (!_excluded.Remove(window)) return;

        if (_exclusionHandlers.Remove(window, out var handlers))
        {
            window.Loaded -= handlers.LoadedUpdate;
            window.LocationChanged -= handlers.LocationUpdate;
            window.SizeChanged -= handlers.SizeUpdate;
            window.IsVisibleChanged -= handlers.VisibilityUpdate;
            window.Closed -= handlers.Closed;
        }

        QueueRegionUpdate();
    }

    private void QueueRegionUpdate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(QueueRegionUpdate);
            return;
        }

        Dispatcher.BeginInvoke(UpdateWindowRegion, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero || !IsLoaded) return;

        var source = PresentationSource.FromVisual(this);
        var transformToDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var dpiX = transformToDevice.M11;
        var dpiY = transformToDevice.M22;

        var overlayWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpiX));
        var overlayHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpiY));
        var region = CreateRectRgn(0, 0, overlayWidth, overlayHeight);
        if (region == IntPtr.Zero) return;

        foreach (var window in _excluded.ToArray())
        {
            if (!IsUsableExclusion(window)) continue;

            var left = (int)Math.Floor((window.Left - Left) * dpiX);
            var top = (int)Math.Floor((window.Top - Top) * dpiY);
            var right = (int)Math.Ceiling((window.Left + window.ActualWidth - Left) * dpiX);
            var bottom = (int)Math.Ceiling((window.Top + window.ActualHeight - Top) * dpiY);

            left = Math.Clamp(left, 0, overlayWidth);
            top = Math.Clamp(top, 0, overlayHeight);
            right = Math.Clamp(right, 0, overlayWidth);
            bottom = Math.Clamp(bottom, 0, overlayHeight);

            if (right <= left || bottom <= top) continue;

            var cutout = CreateRectRgn(left, top, right, bottom);
            if (cutout == IntPtr.Zero) continue;

            CombineRgn(region, region, cutout, RGN_DIFF);
            DeleteObject(cutout);
        }

        if (SetWindowRgn(_hwnd, region, true) == 0)
            DeleteObject(region);
    }

    private static bool IsUsableExclusion(Window window) =>
        window.IsLoaded
        && window.IsVisible
        && window.ActualWidth > 0
        && window.ActualHeight > 0
        && !double.IsNaN(window.Left)
        && !double.IsNaN(window.Top);

    private sealed record ExclusionHandlers(
        RoutedEventHandler LoadedUpdate,
        EventHandler LocationUpdate,
        SizeChangedEventHandler SizeUpdate,
        DependencyPropertyChangedEventHandler VisibilityUpdate,
        EventHandler Closed);

    public void Undo()
    {
        if (_undoHistory.Count == 0) return;
        var act = _undoHistory.Pop();
        act();
        HistoryChanged?.Invoke();
    }

    public void ClearAll()
    {
        RemoveCurrentArrowPreview();
        _suppressHistory = true;
        Ink.Strokes.StrokesChanged -= OnInkStrokesChanged;
        Ink.Strokes = new StrokeCollection();
        Ink.Strokes.StrokesChanged += OnInkStrokesChanged;
        _suppressHistory = false;
        // InkCanvas.Children içinde sadece bizim ok Path'lerimiz var.
        Ink.Children.Clear();
        _undoHistory.Clear();
        HistoryChanged?.Invoke();
    }

    public bool HasStrokes => _undoHistory.Count > 0;
    public event Action? HistoryChanged;

    private void PushUndo(Action action)
    {
        _undoHistory.Push(action);
        HistoryChanged?.Invoke();
    }

    // Prevent Alt+F4 from killing the overlay accidentally — the panel
    // controls its lifetime instead.
    protected override void OnClosing(CancelEventArgs e)
    {
        // Allow normal close from code; it's the keyboard close we want to ignore.
        if (Keyboard.IsKeyDown(Key.F4) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
