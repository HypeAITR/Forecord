using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ScreenRecorder.Views;

public partial class RecordingCompleteDialog : Window
{
    public enum Kind { Recording, Screenshot }

    private readonly string _filePath;
    private readonly DispatcherTimer _autoClose;
    private bool _hovered;

    public RecordingCompleteDialog(string filePath, Kind kind = Kind.Recording)
    {
        InitializeComponent();
        _filePath = filePath;
        FileNameText.Text = Path.GetFileName(filePath);

        if (kind == Kind.Screenshot)
        {
            TitleText.Text       = "Ekran görüntüsü kaydedildi";
            IconText.Text        = "📸";
            IconText.FontSize    = 16;
            IconText.FontWeight  = System.Windows.FontWeights.Normal;
            IconBadge.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
        }

        // Keep the window fixed; the toast content handles fade + translate.
        var work = SystemParameters.WorkArea;
        Top  = work.Bottom - Height - 16;
        Left = work.Right - Width - 16;

        // 6 saniye sonra otomatik kapan (hover yoksa)
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose.Stop();
            if (!_hovered) StartCloseAnimation();
        };
        _autoClose.Start();
    }

    private void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovered = true;
        _autoClose.Stop();
    }

    private void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovered = false;
        _autoClose.Interval = TimeSpan.FromSeconds(3);
        _autoClose.Start();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        App.FileSvc.OpenFile(_filePath);
        StartCloseAnimation();
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        App.FileSvc.ShowInExplorer(_filePath);
        StartCloseAnimation();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => StartCloseAnimation();

    private void StartCloseAnimation()
    {
        if (Resources["SlideOut"] is Storyboard sb)
            sb.Begin(this);
        else
            Close();
    }

    private void SlideOut_Completed(object? sender, EventArgs e) => Close();
}
