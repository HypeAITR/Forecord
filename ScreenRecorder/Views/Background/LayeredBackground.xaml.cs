using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace ScreenRecorder.Views.Background;

public partial class LayeredBackground : WpfUserControl
{
    // Smoothing window for cursor reaction (seconds). 0.22 ~= 220ms target.
    private const double SmoothingSeconds = 0.22;

    // Maximum shader displacement (in UV space). Pure noise jitter on hover.
    private const double MaxStrength = 0.0055;

    private WpfPoint _pointerTarget = new(0.5, 0.5);
    private WpfPoint _pointerCurrent = new(0.5, 0.5);
    private double _intensityTarget;
    private double _intensityCurrent;

    private Window? _window;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastFrame;

    public LayeredBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateRoundedClip();
    }

    private void UpdateRoundedClip()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Clip = new RectangleGeometry(
            new Rect(0, 0, ActualWidth, ActualHeight),
            14, 14);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;

        _window.PreviewMouseMove += OnWindowMouseMove;
        _window.MouseLeave += OnWindowMouseLeave;
        _window.Deactivated += OnWindowDeactivated;
        _clock.Restart();
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null)
        {
            _window.PreviewMouseMove -= OnWindowMouseMove;
            _window.MouseLeave -= OnWindowMouseLeave;
            _window.Deactivated -= OnWindowDeactivated;
        }
        CompositionTarget.Rendering -= OnRendering;
        _window = null;
    }

    private void OnWindowMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_window == null || _window.ActualWidth <= 0 || _window.ActualHeight <= 0) return;

        var p = e.GetPosition(_window);
        _pointerTarget = new WpfPoint(
            Clamp(p.X / _window.ActualWidth, 0, 1),
            Clamp(p.Y / _window.ActualHeight, 0, 1));
        _intensityTarget = 1.0;
    }

    private void OnWindowMouseLeave(object sender, WpfMouseEventArgs e) => _intensityTarget = 0;
    private void OnWindowDeactivated(object? sender, EventArgs e) => _intensityTarget = 0;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Minimized) return;

        var elapsed = _clock.Elapsed;
        var delta = _lastFrame == TimeSpan.Zero ? TimeSpan.FromMilliseconds(16) : elapsed - _lastFrame;
        _lastFrame = elapsed;

        var damping = 1 - Math.Exp(-delta.TotalSeconds / SmoothingSeconds);
        _pointerCurrent.X += (_pointerTarget.X - _pointerCurrent.X) * damping;
        _pointerCurrent.Y += (_pointerTarget.Y - _pointerCurrent.Y) * damping;
        _intensityCurrent += (_intensityTarget - _intensityCurrent) * damping;

        // Calm the effect further while recording so it does not steal attention from the UI.
        var attenuation = GetDataContextBool("IsRecording") ? 0.45 : 1.0;
        if (GetDataContextBool("IsPaused")) attenuation = Math.Max(attenuation, 0.7);

        CursorDisplacement.Center = _pointerCurrent;
        CursorDisplacement.Strength = _intensityCurrent * MaxStrength * attenuation;
    }

    private bool GetDataContextBool(string propertyName)
    {
        var property = DataContext?.GetType().GetProperty(propertyName);
        return property?.GetValue(DataContext) is true;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(max, value));
}
