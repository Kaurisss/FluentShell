using Microsoft.UI.Xaml.Data;

namespace FluentShell.Views.Converters;

public sealed class PercentToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double percent)
            return $"{percent:F0}%";

        return "0%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
