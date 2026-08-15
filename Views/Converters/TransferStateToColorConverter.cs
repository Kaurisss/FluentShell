using FluentShell.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Converters;

public sealed class TransferStateToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not TransferItemState state)
            return new SolidColorBrush(Colors.Gray);

        return state switch
        {
            TransferItemState.Pending => new SolidColorBrush(Colors.Gray),
            TransferItemState.Transferring => new SolidColorBrush(Colors.DodgerBlue),
            TransferItemState.Completed => new SolidColorBrush(Colors.Green),
            TransferItemState.Skipped => new SolidColorBrush(Colors.Orange),
            TransferItemState.Failed => new SolidColorBrush(Colors.Red),
            _ => new SolidColorBrush(Colors.Gray)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
