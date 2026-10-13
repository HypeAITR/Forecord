using System.ComponentModel;
using System.Runtime.CompilerServices;
using WpfColor = System.Windows.Media.Color;
using ScreenRecorder.Models;

namespace ScreenRecorder.ViewModels;

/// <summary>
/// Holds drawing tool state shared between the toolbar and the overlay
/// window. Both subscribe to PropertyChanged.
/// </summary>
public sealed class DrawingToolViewModel : INotifyPropertyChanged
{
    public static readonly WpfColor[] Palette =
    {
        WpfColor.FromRgb(0xFF, 0x45, 0x3A), // Kırmızı (accent)
        WpfColor.FromRgb(0xFF, 0x9F, 0x0A), // Sarı/turuncu
        WpfColor.FromRgb(0x32, 0xD7, 0x4B), // Yeşil
        WpfColor.FromRgb(0x0A, 0x84, 0xFF), // Mavi
        WpfColor.FromRgb(0xFF, 0xFF, 0xFF), // Beyaz
        WpfColor.FromRgb(0x10, 0x10, 0x10), // Siyah
    };

    public const double MinThickness = 1.0;
    public const double MaxThickness = 40.0;

    private DrawingTool _tool = DrawingTool.None;
    public  DrawingTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPen));
            OnPropertyChanged(nameof(IsEraser));
            OnPropertyChanged(nameof(IsArrow));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ActiveThickness));
        }
    }

    public bool IsPen     => _tool == DrawingTool.Pen;
    public bool IsEraser  => _tool == DrawingTool.Eraser;
    public bool IsArrow   => _tool == DrawingTool.Arrow;
    public bool IsActive  => _tool != DrawingTool.None;

    private WpfColor _color = Palette[0];
    public  WpfColor Color
    {
        get => _color;
        set { _color = value; OnPropertyChanged(); }
    }

    private double _penThickness = 4.5;
    public  double PenThickness
    {
        get => _penThickness;
        set
        {
            if (Math.Abs(_penThickness - value) < 0.01) return;
            _penThickness = value;
            OnPropertyChanged();
            if (IsPen) OnPropertyChanged(nameof(ActiveThickness));
        }
    }

    private double _eraserThickness = 22;
    public  double EraserThickness
    {
        get => _eraserThickness;
        set
        {
            if (Math.Abs(_eraserThickness - value) < 0.01) return;
            _eraserThickness = value;
            OnPropertyChanged();
            if (IsEraser) OnPropertyChanged(nameof(ActiveThickness));
        }
    }

    /// <summary>
    /// Slider sürükledikçe aktif aracın kendi kalınlığını güncelliyor.
    /// Pen ↔ Eraser geçişinde yeni aracın kendi son değeri görünüyor.
    /// </summary>
    public double ActiveThickness
    {
        get => IsEraser ? _eraserThickness : _penThickness;
        set
        {
            if (IsEraser) EraserThickness = value;
            else          PenThickness    = value;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
