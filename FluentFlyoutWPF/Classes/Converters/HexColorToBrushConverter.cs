using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FluentFlyoutWPF.Classes.Converters;

public sealed class HexColorToBrushConverter : IValueConverter
{
    private static readonly Brush DefaultBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text && (text.Length == 7 || text.Length == 9)
            && text[0] == '#' && ColorConverter.ConvertFromString(text) is Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
