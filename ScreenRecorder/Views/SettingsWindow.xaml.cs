using System.Windows;
using System.Windows.Input;
using ScreenRecorder.ViewModels;

namespace ScreenRecorder.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        _vm            = new SettingsViewModel(App.SettingsSvc);
        DataContext    = _vm;
        _vm.Saved     += () => { DialogResult = true; Close(); };
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
