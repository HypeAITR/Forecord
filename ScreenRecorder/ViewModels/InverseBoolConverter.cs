using System.Globalization;
using System.Windows.Data;

namespace ScreenRecorder.ViewModels;

/// <summary>bool'u tersine çevirir – XAML'de DataTrigger yerine basit IsEnabled bağlaması için.</summary>
public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is bool b ? !b : value;
}
