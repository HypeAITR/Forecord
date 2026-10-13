using System.Windows;

namespace ScreenRecorder.Views;

public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    private ConfirmDialog(string title, string message, string confirmText, string cancelText, bool destructive)
    {
        InitializeComponent();
        TitleText.Text   = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmText;
        CancelBtn.Content  = cancelText;
        if (!destructive)
        {
            ConfirmBtn.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
        }
    }

    public static bool Show(Window? owner, string title, string message,
                            string confirmText = "Tamam", string cancelText = "Vazgeç",
                            bool destructive = false)
    {
        var dlg = new ConfirmDialog(title, message, confirmText, cancelText, destructive)
        {
            Owner = owner ?? System.Windows.Application.Current.Windows
                .OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) { Result = true; Close(); }
    private void Cancel_Click (object sender, RoutedEventArgs e) { Result = false; Close(); }
}
