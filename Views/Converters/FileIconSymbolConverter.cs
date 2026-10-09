using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FluentShell.Views.Converters;

/// <summary>Maps file-list item kinds to the shared Fluent icon library.</summary>
public sealed class FileIconSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Symbol.Folder : Symbol.Document;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        DependencyProperty.UnsetValue;
}
