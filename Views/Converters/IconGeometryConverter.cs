using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentShell.Views.Converters;

/// <summary>Creates a separate geometry for each icon using shared vector path text.</summary>
public sealed class IconGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Parse((string)value);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        DependencyProperty.UnsetValue;

    public static Geometry Parse(string data)
    {
        var artwork = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

        // Preserve the SVG's full 0..20 viewport, including its transparent inset.
        // The zero-area line extends the geometry bounds without painting a frame.
        // This keeps antialiasing coverage away from PathIcon's tight render bounds.
        var viewport = new GeometryGroup { FillRule = FillRule.Nonzero };
        viewport.Children.Add(new LineGeometry { StartPoint = new Point(0, 0), EndPoint = new Point(20, 20) });
        viewport.Children.Add(artwork);
        return viewport;
    }
}
