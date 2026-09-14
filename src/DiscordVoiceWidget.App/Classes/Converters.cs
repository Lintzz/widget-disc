using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiscordVoiceWidget.App;

/// <summary>
/// Complementa o BooleanToVisibilityConverter embutido do WPF, que nao tem
/// modo invertido.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed or Visibility.Hidden;
}
