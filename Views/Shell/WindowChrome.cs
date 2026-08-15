using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public static class WindowChrome
{
    public static void ApplyTheme(
        AppWindow appWindow,
        NavigationView navigationView,
        DispatcherQueue dispatcherQueue,
        string theme)
    {
        dispatcherQueue.TryEnqueue(() =>
        {
            ApplyTitleBarColors(appWindow, navigationView.ActualTheme, theme);
            var pane = FindVisualChild<SplitView>(navigationView);
            if (pane is not null) pane.PaneBackground = new SolidColorBrush(Colors.Transparent);
        });
    }

    private static void ApplyTitleBarColors(AppWindow appWindow, ElementTheme actualTheme, string theme)
    {
        var useDark = theme == "深色" || (theme == "系统" && actualTheme == ElementTheme.Dark);
        var foreground = useDark ? Colors.White : Colors.Black;
        var inactiveForeground = useDark
            ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
            : Windows.UI.Color.FromArgb(160, 0, 0, 0);
        var hoverBackground = useDark
            ? Windows.UI.Color.FromArgb(32, 255, 255, 255)
            : Windows.UI.Color.FromArgb(20, 0, 0, 0);
        var pressedBackground = useDark
            ? Windows.UI.Color.FromArgb(48, 255, 255, 255)
            : Windows.UI.Color.FromArgb(32, 0, 0, 0);
        var titleBar = appWindow.TitleBar;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}