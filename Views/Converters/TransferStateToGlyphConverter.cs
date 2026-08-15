using FluentShell.Core;
using Microsoft.UI.Xaml.Data;

namespace FluentShell.Views.Converters;

public sealed class TransferStateToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not TransferItemState state) return ""; // Default circle

        return state switch
        {
            TransferItemState.Pending => "", // Circle (waiting)
            TransferItemState.Transferring => "", // Circle (in progress)
            TransferItemState.Completed => "", // CheckMark
            TransferItemState.Skipped => "", // Minus
            TransferItemState.Failed => "", // Error
            _ => ""
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
