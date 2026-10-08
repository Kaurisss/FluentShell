using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Content;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace FluentShell.Views.Shell;

public static class WindowChrome
{
    public static void EnableTitleBarInputRegions(Window window, FrameworkElement root, FrameworkElement titleBar, params FrameworkElement[] controls)
    {
        var input = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        RectInt32[]? previousRects = null;
        RectInt32[]? previousCaptionRects = null;
        XamlRoot? observedRoot = null;
        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateRegions();
        EventHandler<object> layoutUpdated = (_, _) => UpdateRegions();
        void Loaded(object sender, RoutedEventArgs args) => UpdateRegions();

        void UpdateRegions()
        {
            if (!root.IsLoaded || root.XamlRoot is null || !window.ExtendsContentIntoTitleBar) return;
            if (!ReferenceEquals(observedRoot, root.XamlRoot))
            {
                if (observedRoot is not null) observedRoot.Changed -= RootChanged;
                observedRoot = root.XamlRoot;
                observedRoot.Changed += RootChanged;
            }

            var windowCoordinates = ContentCoordinateConverter.CreateForWindowId(window.AppWindow.Id);
            RectInt32 GetBounds(FrameworkElement control)
            {
                var localBounds = control.TransformToVisual(root).TransformBounds(
                    new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                var bounds = windowCoordinates.ConvertScreenToLocal(
                    root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(localBounds));
                var left = (int)Math.Floor(bounds.X);
                var top = (int)Math.Floor(bounds.Y);
                return new RectInt32(left, top,
                    (int)Math.Ceiling(bounds.Right) - left,
                    (int)Math.Ceiling(bounds.Bottom) - top);
            }
            var rects = controls.Where(IsVisible).Select(GetBounds).ToArray();
            var titleBounds = GetBounds(titleBar);
            var captionRects = new List<RectInt32>();
            var captionLeft = titleBounds.X + window.AppWindow.TitleBar.LeftInset;
            var captionRight = titleBounds.X + titleBounds.Width - window.AppWindow.TitleBar.RightInset;
            foreach (var rect in rects.OrderBy(rect => rect.X))
            {
                var next = Math.Min(captionRight, rect.X);
                if (next > captionLeft)
                    captionRects.Add(new RectInt32(captionLeft, titleBounds.Y, next - captionLeft, titleBounds.Height));
                captionLeft = Math.Max(captionLeft, rect.X + rect.Width);
            }
            if (captionRight > captionLeft)
                captionRects.Add(new RectInt32(captionLeft, titleBounds.Y, captionRight - captionLeft, titleBounds.Height));
            var captionArray = captionRects.ToArray();
            if (previousRects is not null && previousRects.SequenceEqual(rects) &&
                previousCaptionRects is not null && previousCaptionRects.SequenceEqual(captionArray)) return;

            // Own the caption map here; Window.SetTitleBar would overwrite these regions.
            input.SetRegionRects(NonClientRegionKind.Caption, captionArray);
            input.SetRegionRects(NonClientRegionKind.Passthrough, rects);
            previousRects = rects;
            previousCaptionRects = captionArray;
        }

        static bool IsVisible(FrameworkElement control)
        {
            if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
            for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            return true;
        }

        // Layout changes also cover adding/closing tabs and ancestor visibility changes.
        root.LayoutUpdated += layoutUpdated;
        root.Loaded += Loaded;
        window.Closed += (_, _) =>
        {
            root.LayoutUpdated -= layoutUpdated;
            root.Loaded -= Loaded;
            if (observedRoot is not null) observedRoot.Changed -= RootChanged;
        };
        UpdateRegions();
    }

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

    public static void ApplyTitleBarColors(AppWindow appWindow, ElementTheme actualTheme, string theme)
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
