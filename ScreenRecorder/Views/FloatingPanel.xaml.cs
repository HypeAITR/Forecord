using System.Runtime.InteropServices;
using System.Windows;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.ViewModels;

namespace ScreenRecorder.Views;

public partial class FloatingPanel : Window
{
    // WDA_EXCLUDEFROMCAPTURE – bu pencereyi ekran kayıtlarından gizler (Win10 2004+)
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public FloatingPanel(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Konumlandırma SizeToContent nedeniyle Loaded'a kadar Width'i bilmiyoruz.
        Top = 12;

        Loaded += (_, _) =>
        {
            var screen = SystemParameters.WorkArea;
            Left = (screen.Width - ActualWidth) / 2;

            var hwnd = new WindowInteropHelper(this).Handle;
            // Hata alınırsa (eski Windows) sessizce devam et
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        };
    }

    private void Panel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        // Buton tıklamasını DragMove yutmasın: tıklanan eleman herhangi bir
        // ButtonBase (Button / ToggleButton) ataya sahipse drag başlatma.
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null)
            return;
        DragMove();
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start != null)
        {
            if (start is T t) return t;
            start = VisualTreeHelper.GetParent(start);
        }
        return null;
    }
}
