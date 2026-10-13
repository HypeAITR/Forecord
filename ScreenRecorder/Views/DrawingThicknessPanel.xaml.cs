using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.Models;
using ScreenRecorder.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ScreenRecorder.Views;

public partial class DrawingThicknessPanel : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // Stroke kalınlığı önizleme kutusunun yüksekliğini aşmasın diye en fazla
    // bu değere clamp'lenir. Slider 1-100 değer alabilir, ama görsel önizleme
    // kutuya sığacak şekilde sınırlandırılır.
    private const double PreviewMaxStroke = 26;

    private readonly DrawingToolViewModel _vm;
    private readonly Window _anchor;
    private bool _suppressSlider;

    public DrawingThicknessPanel(DrawingToolViewModel vm, Window anchor)
    {
        _vm     = vm;
        _anchor = anchor;
        _suppressSlider = true;
        InitializeComponent();
        _suppressSlider = false;

        PanelColorList.ItemsSource = DrawingToolViewModel.Palette
            .Select(c => (WpfBrush)new WpfSolidColorBrush(c)).ToList();

        _vm.PropertyChanged    += OnVmChanged;
        _anchor.LocationChanged += (_, _) => Reposition();
        _anchor.SizeChanged     += (_, _) => Reposition();

        Loaded += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
            catch { }
            Reposition();
            SyncFromVm();
        };
        Closed += (_, _) => _vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DrawingToolViewModel.Tool)
                           or nameof(DrawingToolViewModel.PenThickness)
                           or nameof(DrawingToolViewModel.EraserThickness)
                           or nameof(DrawingToolViewModel.ActiveThickness)
                           or nameof(DrawingToolViewModel.Color))
        {
            Dispatcher.Invoke(SyncFromVm);
        }
    }

    private void SyncFromVm()
    {
        var t = _vm.ActiveThickness;
        _suppressSlider = true;
        SizeSlider.Value = t;
        _suppressSlider = false;

        ValueText.Text = $"{Math.Round(t)} px";

        // Stroke kalınlığını önizleme kutusu yüksekliğine clamp'le.
        PreviewWave.StrokeThickness = Math.Max(2, Math.Min(t, PreviewMaxStroke));
        if (PreviewWave.Data == null) PreviewWave.Data = BuildWaveGeometry();

        if (_vm.IsEraser)
        {
            LabelText.Text = "Silgi boyutu";
            PreviewWave.Opacity = 0.35;
        }
        else
        {
            LabelText.Text = _vm.IsArrow ? "Ok kalınlığı" : "Kalem boyutu";
            PreviewWave.Opacity = 1.0;
        }
        if (SmoothBtn != null) SmoothBtn.IsChecked = !_vm.IsArrow;
        if (ArrowBtn != null) ArrowBtn.IsChecked = _vm.IsArrow;
    }

    private static Geometry BuildWaveGeometry()
    {
        var fig = new PathFigure { StartPoint = new System.Windows.Point(0, 16) };
        fig.Segments.Add(new BezierSegment(
            new System.Windows.Point( 60,  -2),
            new System.Windows.Point(120,  34),
            new System.Windows.Point(180,  16), true));
        fig.Segments.Add(new BezierSegment(
            new System.Windows.Point(240,  -2),
            new System.Windows.Point(300,  34),
            new System.Windows.Point(360,  16), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private void SizeSlider_OnValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider) return;
        if (!_vm.IsActive) _vm.Tool = DrawingTool.Pen;
        _vm.ActiveThickness = e.NewValue;
    }

    private void ColorDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is WpfSolidColorBrush b)
        {
            _vm.Color = b.Color;
            if (!_vm.IsPen) _vm.Tool = DrawingTool.Pen;
        }
    }

    private void Chevron_Click(object sender, RoutedEventArgs e) => Close();

    private void Smooth_Click(object sender, RoutedEventArgs e)
    {
        _vm.Tool = DrawingTool.Pen;
        // Yumuşatma toggle: InkCanvas DefaultDrawingAttributes.FitToCurve
        // davranışını yansıtacak şekilde bayrak. Şimdilik sadece görsel toggle.
    }

    private void Arrow_Click(object sender, RoutedEventArgs e)
    {
        // Toggle: tekrar tıklanırsa pen'e dön.
        _vm.Tool = ArrowBtn.IsChecked == true ? DrawingTool.Arrow : DrawingTool.Pen;
    }

    private void Reposition()
    {
        if (!IsLoaded) return;
        const int gap = 8;
        var screen = SystemParameters.WorkArea;
        var x = _anchor.Left + (_anchor.ActualWidth - ActualWidth) / 2;
        var y = _anchor.Top + _anchor.ActualHeight + gap;
        if (y + ActualHeight > screen.Bottom - 4)
            y = _anchor.Top - ActualHeight - gap;
        Left = Math.Max(screen.Left + 4, Math.Min(x, screen.Right - ActualWidth - 4));
        Top  = Math.Max(screen.Top + 4,  y);
    }
}
