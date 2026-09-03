using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed class BoolToTextWrappingConverter : IValueConverter
{
    // The log viewer binds this once per rendered row, so the two possible results are boxed
    // once here instead of allocating on every row and on every wrap toggle.
    private static readonly object Wrap = TextWrapping.Wrap;
    private static readonly object NoWrap = TextWrapping.NoWrap;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Wrap : NoWrap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
