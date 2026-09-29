using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

/// <summary>Displays the shortcut as separate, theme-aware key caps.</summary>
public sealed class ShortcutKeyPanel : StackPanel
{
    public ShortcutKeyPanel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        ActualThemeChanged += (_, _) => ApplyTheme();
    }

    public void SetKey(string key)
    {
        Children.Clear();
        foreach (var label in new[] { "Ctrl", "Shift", key })
        {
            Children.Add(new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                Child = new TextBlock { Text = label, FontSize = 13 }
            });
        }
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        foreach (var cap in Children.OfType<Border>())
            cap.BorderBrush = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Gray : Microsoft.UI.Colors.Silver);
    }
}

