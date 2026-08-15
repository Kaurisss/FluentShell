using FluentShell.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FluentShell.Views.Converters;

public sealed class TransferStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not TransferItemState state)
            return Visibility.Collapsed;

        return state == TransferItemState.Transferring
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
