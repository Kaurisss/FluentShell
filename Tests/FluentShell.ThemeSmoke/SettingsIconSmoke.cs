using SymbolIcon = FluentIcons.WinUI.SymbolIcon;
using CommunityToolkit.WinUI.Controls;
using FluentShell.Models;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifySettingsIconsAsync()
    {
        _window = new Window { Title = "FluentShell FluentIcons settings" };
        var root = new Grid { Width = 800, Height = 800, RequestedTheme = ElementTheme.Light };
        _window.Content = root;
        var page = new SettingsPage(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        page.SetSettings(new AppSettings(), "Offline fixture");
        root.Children.Add(page);
        _window.Activate();
        await Task.Delay(250);
        var navigate = typeof(SettingsPage).GetMethod("NavigateCategory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var categories = new (string? Key, string Page)[]
        {
            (null, "SettingsHome"), ("appearance", "AppearancePage"), ("terminal", "TerminalColorsPage"),
            ("connection", "ConnectionPage"), ("transfer", "TransferSettingsPage"),
            ("shortcuts", "ShortcutSettingsPage"), ("data", "DataSettingsPage"), ("about", "AboutSettingsPage")
        };
        var failures = new List<string>();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            root.Background = new SolidColorBrush(theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
            foreach (var (key, pageName) in categories)
            {
                navigate.Invoke(page, [key]);
                await Task.Delay(300);
                root.UpdateLayout();
                var scroller = (ScrollViewer)page.FindName(pageName);
                var cards = Descendants(scroller).OfType<SettingsCard>().ToArray();
                var icons = cards.Select(card => new
                {
                    header = card.Header,
                    symbol = (card.HeaderIcon as SymbolIcon)?.Symbol.ToString(),
                    width = (card.HeaderIcon as SymbolIcon)?.ActualWidth,
                    height = (card.HeaderIcon as SymbolIcon)?.ActualHeight,
                    fontSize = (card.HeaderIcon as SymbolIcon)?.FontSize,
                    glyph = (card.HeaderIcon as SymbolIcon)?.Glyph
                }).ToArray();
                var fluentIcons = cards.Length > 0 && icons.All(icon => icon.symbol is not null && icon.width == 20 && icon.height == 20 && icon.fontSize == 20 && !string.IsNullOrWhiteSpace(icon.glyph));
                Program.Results.Add(new { control = "FluentIcons settings card icons", theme = theme.ToString(), category = key ?? "home", icons, passed = fluentIcons });
                if (!fluentIcons) failures.Add($"{theme}/{key ?? "home"}: all card header icons must be 20 x 20 FluentIcons.");
                await CaptureAsync(root, Program.ReportPath + $".{theme}.{key ?? "home"}.png");
                if (key != "transfer") continue;
                var help = (Button)page.FindName("PageHelpButton");
                var helpIcon = Descendants(help).OfType<FluentIcons.WinUI.FluentIcon>().Single();
                var bounds = helpIcon.TransformToVisual(help).TransformBounds(new Rect(0, 0, helpIcon.ActualWidth, helpIcon.ActualHeight));
                var compactHelp = help.ActualWidth == 24 && help.ActualHeight == 24
                    && helpIcon.Icon == FluentIcons.Common.Icon.ErrorCircle && helpIcon.IconSize == FluentIcons.Common.IconSize.Size16
                    && helpIcon.FontSize == 14 && !helpIcon.IsTextScaleFactorEnabled
                    && Math.Abs(bounds.Width - 16) < 0.01 && Math.Abs(bounds.Height - 16) < 0.01
                    && Math.Abs(bounds.X - 4) < 0.01 && Math.Abs(bounds.Y - 4) < 0.01;
                Program.Results.Add(new { control = "compact FluentIcons help icon", theme = theme.ToString(), bounds, passed = compactHelp });
                if (!compactHelp) failures.Add($"{theme}: FluentIcons help artwork must remain 16 x 16 and centered in its 24 x 24 button.");
                await CaptureAsync(helpIcon, Program.ReportPath + $".{theme}.help.png");
                foreach (var scale in new[] { 1d, 1.25d, 1.5d })
                {
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(helpIcon, (int)(16 * scale), (int)(16 * scale));
                    var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                    var hasArtwork = false;
                    var clearEdges = true;
                    for (var y = 0; y < bitmap.PixelHeight; y++)
                        for (var x = 0; x < bitmap.PixelWidth; x++)
                        {
                            var alpha = pixels[(y * bitmap.PixelWidth + x) * 4 + 3];
                            hasArtwork |= alpha > 0;
                            if (x == 0 || y == 0 || x == bitmap.PixelWidth - 1 || y == bitmap.PixelHeight - 1)
                            {
                                clearEdges &= alpha == 0;
                            }
                        }
                    Program.Results.Add(new { control = "help icon edge coverage", theme = theme.ToString(), scale, bitmap.PixelWidth, bitmap.PixelHeight, hasArtwork, clearEdges, passed = hasArtwork && clearEdges });
                    if (!hasArtwork || !clearEdges) failures.Add($"{theme}/{scale}: the help icon must render completely inside its viewport.");
                }
                _window.Activate();
                help.Focus(FocusState.Programmatic);
                help.Flyout.ShowAt(help);
                await Task.Delay(250);
                var helpShown = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(popup => popup.IsOpen)
                    && !string.IsNullOrWhiteSpace(((TextBlock)page.FindName("PageHelpText")).Text);
                Program.Results.Add(new { control = "settings help flyout", theme = theme.ToString(), passed = helpShown });
                if (!helpShown) failures.Add($"{theme}: the help flyout must display the existing explanation.");
                await HideFlyoutAsync(help.Flyout);
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Program.Finish();
    }
}
