using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public abstract class WindowBackdrop : DependencyObject
{
    // Keep the selected material on the owner root so secondary windows can
    // observe changes even while their session is detached from the main view.
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.RegisterAttached(
        "Material", typeof(string), typeof(WindowBackdrop), new PropertyMetadata("Mica"));

    public static string GetMaterial(DependencyObject element) => (string)element.GetValue(MaterialProperty);
    public static void SetMaterial(DependencyObject element, string material) => element.SetValue(MaterialProperty, material);

    public static void Apply(Window window, string material)
    {
        var root = window.Content as DependencyObject;
        if (window.SystemBackdrop is not null && root is not null && GetMaterial(root) == material) return;
        // Each window owns its own controller and backdrop lifetime.
        window.SystemBackdrop = material switch
        {
            "Mica Alt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            "亚克力" => new DesktopAcrylicBackdrop(),
            "Acrylic Thin" => new ThinAcrylicBackdrop(),
            _ => new MicaBackdrop()
        };
        if (root is not null) SetMaterial(root, material);
    }
}
