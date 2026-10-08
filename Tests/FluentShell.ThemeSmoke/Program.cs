using System.Reflection;
using System.Text.Json;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views;
using FluentShell.Views.Session;
using FluentShell.Views.Shell;
using FluentShell.Views.Dialogs;
using FluentShell.Views.Converters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.WinUI.Controls;

namespace FluentShell.ThemeSmoke;

internal static class Program
{
    internal static readonly List<object> Results = [];
    internal static string ReportPath = Path.GetFullPath("theme-smoke.json");
    internal static bool SftpDeleteOnly;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0) ReportPath = Path.GetFullPath(args[0]);
        SftpDeleteOnly = args.Contains("--sftp-delete-smoke");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new SmokeApp();
            });
        }
        catch (Exception e) { Finish(e); }
    }

    internal static void Finish(Exception? error = null)
    {
        File.WriteAllText(ReportPath, JsonSerializer.Serialize(new { passed = error is null, error = error?.ToString(), checks = Results },
            new JsonSerializerOptions { WriteIndented = true }));
        Environment.Exit(error is null ? 0 : 1);
    }
}

// Reuse real application resources, but never launch MainWindow or load user profiles.
internal sealed class SmokeApp : App
{
    private Window? _window;
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, error) =>
        {
            error.Handled = true;
            Program.Finish(error.Exception);
        };
        try
        {
            if (Program.SftpDeleteOnly)
            {
                await VerifySftpDeleteAsync();
                return;
            }
            _window = new Window { Title = "FluentShell offline theme regression" };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 1050));
            var root = new Grid { RequestedTheme = ElementTheme.Light, Background = new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            _window.Content = root;
            var profile = new ServerProfile { Name = "Offline theme check", Host = "offline.invalid", Username = "test" };
            var sidebar = new ConnectedServerSidebar();
            sidebar.UpdateSession(profile, SessionConnectionState.Connected);
            sidebar.UpdateMetrics(profile.Id, new ServerMetrics { CpuPercent = 42, MemoryPercent = 34, SwapPercent = 12, LoadAverage = "0.42" }, true);
            root.Children.Add(sidebar);
            var workspace = new SessionWorkspace(profile, WinRT.Interop.WindowNative.GetWindowHandle(_window),
                (_, _) => Task.FromResult<ISshConnection?>(null), _ => Task.FromResult(false),
                () => Task.FromResult<string?>(null));
            Grid.SetColumn(workspace, 1);
            root.Children.Add(workspace);
            _window.Activate();
            var highContrast = (ResourceDictionary)Resources.ThemeDictionaries["HighContrast"];
            var highContrastKeys = new[] { "PageSurfaceBrush", "PanelSurfaceBrush", "MutedTextBrush", "SubtleStrokeBrush",
                "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground" };
            if (highContrastKeys.Any(key => highContrast[key] is not SolidColorBrush))
                throw new InvalidOperationException("High contrast application brushes must still resolve.");
            Program.Results.Add(new { control = "high contrast resource resolution", passed = true });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var terminal = Field<TerminalPane>(workspace, "_terminalPane");
            var web = Field<WebView2>(terminal, "_terminalView");
            while (!Field<bool>(terminal, "_ready")) await Task.Delay(100, timeout.Token);
            terminal.Write("Offline output survives theme changes\r\n");
            var failures = new List<string>();
            await VerifyTerminalAsync(terminal, web, failures);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                // Match navigating away to settings and returning to the cached session.
                root.Children.Remove(workspace);
                await Task.Delay(100, timeout.Token);
                root.RequestedTheme = theme;
                root.Children.Add(workspace);
                await Task.Delay(400, timeout.Token);
                root.UpdateLayout();
                var sftp = Field<SftpWorkspaceView>(workspace, "_sftpView");
                foreach (var element in new FrameworkElement[] { workspace, terminal, sftp,
                    (FrameworkElement)sftp.FindName("LocalFiles"), (FrameworkElement)sftp.FindName("RemoteTable"),
                    (FrameworkElement)sftp.FindName("LocalPathBox"), (FrameworkElement)sftp.FindName("PathBox") })
                {
                    var passed = element.ActualTheme == theme;
                    Program.Results.Add(new { phase = theme.ToString(), control = element.GetType().Name, expected = theme.ToString(), actual = element.ActualTheme.ToString(), passed });
                    if (!passed) failures.Add($"{element.GetType().Name}: expected {theme}, got {element.ActualTheme}");
                }
                var metrics = (StackPanel)sidebar.FindName("MetricsPanel");
                var expected = theme == ElementTheme.Dark ? "#FFB5B5B5" : "#FF5C5C5C";
                var metricTexts = Descendants(metrics).OfType<TextBlock>().Where(t => t.Text is "42%" or "负载").ToArray();
                if (metricTexts.Length != 2) throw new InvalidOperationException("Expected both dynamic metric text controls.");
                foreach (var text in metricTexts)
                {
                    var color = ((SolidColorBrush)text.Foreground).Color.ToString();
                    var passed = string.Equals(expected, color, StringComparison.OrdinalIgnoreCase);
                    Program.Results.Add(new { phase = theme.ToString(), control = "Metric " + text.Text, expected, actual = color, passed });
                    if (!passed) failures.Add($"Metric {text.Text}: expected {expected}, got {color}");
                }
                var scheme = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.documentElement.style.colorScheme"));
                var background = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor"));
                var expectedScheme = theme == ElementTheme.Dark ? "dark" : "light";
                var expectedBackground = theme == ElementTheme.Dark ? "rgb(54, 54, 54)" : "rgb(254, 254, 254)";
                var webPassed = scheme == expectedScheme && background == expectedBackground;
                Program.Results.Add(new { phase = theme.ToString(), control = "xterm page", scheme, background, passed = webPassed });
                if (!webPassed) failures.Add($"xterm expected {expectedScheme}/{expectedBackground}, got {scheme}/{background}");
            }
            await VerifyTransferAndFileFlyoutsAsync(root, Field<SftpWorkspaceView>(workspace, "_sftpView"), failures);
            // Exercise the settings editor without touching user settings or SSH.
            root.Children.Remove(workspace);
            var settingsPage = new SettingsPage(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            Grid.SetColumn(settingsPage, 1);
            root.Children.Add(settingsPage);
            settingsPage.SetSettings(new AppSettings(), "Offline fixture");
            root.UpdateLayout();
            await Task.Delay(200, timeout.Token);
            await VerifyTextFlyoutAsync(root, failures);
            foreach (var iconName in new[] { "Copy", "Paste", "Confirm" })
                foreach (var scale in new[] { 1d, 1.25d, 1.5d })
                    await VerifyIconEdgesAsync(settingsPage, root, iconName, scale, failures);
            var homePanel = (StackPanel)((ScrollViewer)settingsPage.FindName("SettingsHome")).Content;
            if (homePanel.Children.OfType<SettingsCard>().Count() != 7) failures.Add("Settings home must contain seven category cards.");
            var cardBounds = homePanel.Children.OfType<SettingsCard>().First();
            Program.Results.Add(new { control = "settings card layout", rootWidth = root.ActualWidth, settingsWidth = settingsPage.ActualWidth,
                panelWidth = homePanel.ActualWidth, cardWidth = cardBounds.ActualWidth,
                panelX = homePanel.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()).X,
                cardX = cardBounds.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()).X });
            var cardRight = cardBounds.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(cardBounds.ActualWidth, 0)).X;
            if (cardRight > root.ActualWidth + 1) failures.Add("Settings cards must fit within the viewport.");
            await CaptureAsync(root, Program.ReportPath + ".home.png");
            foreach (var key in new[] { "appearance", "terminal", "connection", "transfer", "shortcuts", "data", "about" })
            {
                typeof(SettingsPage).GetMethod("ShowCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settingsPage, new object[] { key });
                root.UpdateLayout();
                var crumbs = ((BreadcrumbBar)settingsPage.FindName("SettingsBreadcrumb")).ItemsSource as string[];
                if (crumbs is not { Length: 2 } || crumbs[0] != "设置") failures.Add("Category breadcrumb must include root and current page.");
                if (((ScrollViewer)settingsPage.FindName("SettingsHome")).Visibility != Visibility.Collapsed) failures.Add("Category navigation must hide home.");
                await Task.Delay(240, timeout.Token);
                await CaptureAsync(root, Program.ReportPath + ".settings." + key + ".png");
                if (key == "terminal")
                {
                    root.RequestedTheme = ElementTheme.Dark;
                    root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                    await Task.Delay(200, timeout.Token);
                    await CaptureAsync(root, Program.ReportPath + ".settings.terminal.dark.png");
                    root.RequestedTheme = ElementTheme.Light;
                    root.Background = new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke);
                }
                if (key == "about")
                {
                    var aboutPage = (ScrollViewer)settingsPage.FindName("AboutSettingsPage");
                    var links = Descendants(aboutPage).OfType<HyperlinkButton>().ToArray();
                    if (links.Length != 6 || links.Any(link => link.NavigateUri is null
                        || link.NavigateUri.Scheme != "https" || link.NavigateUri.Host != "github.com"
                        || !link.NavigateUri.AbsolutePath.StartsWith("/Kaurisss/FluentShell", StringComparison.Ordinal)
                        || !link.IsTabStop || !link.IsEnabled))
                        failures.Add("About page must expose six keyboard-accessible FluentShell project links.");
                    var versionText = (TextBlock)settingsPage.FindName("VersionText");
                    if (!versionText.Text.StartsWith("版本 ", StringComparison.Ordinal) || versionText.Text.Contains('\n'))
                        failures.Add("About version must be separate from system diagnostics.");
                    if (((TextBlock)settingsPage.FindName("DiagnosticSummaryText")).Text != DiagnosticLog.Summary)
                        failures.Add("Visible diagnostics must match the copied summary.");
                    if (((Image)settingsPage.FindName("AboutAppLogo")).Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 })
                        failures.Add("About app logo must load successfully.");
                    root.RequestedTheme = ElementTheme.Dark;
                    root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                    await Task.Delay(200, timeout.Token);
                    await CaptureAsync(root, Program.ReportPath + ".settings.about.dark.png");
                    root.ColumnDefinitions[0].Width = new GridLength(0);
                    settingsPage.UpdateResponsiveLayout(16);
                    // Constrain the viewport directly so layout verification also works with a hidden test window.
                    root.Width = 560;
                    root.Height = 760;
                    await Task.Delay(200, timeout.Token);
                    root.UpdateLayout();
                    if (root.ActualWidth > 561 || aboutPage.ScrollableHeight <= 0)
                        failures.Add("About narrow layout must use a 560-pixel scrolling viewport.");
                    foreach (var link in links)
                    {
                        var bounds = link.TransformToVisual(aboutPage).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, link.ActualWidth, link.ActualHeight));
                        if (bounds.Left < 0 || bounds.Right > aboutPage.ActualWidth + 1 || link.ActualWidth <= 0)
                            failures.Add("About link is clipped in the narrow layout: " + link.Content);
                    }
                    await CaptureAsync(root, Program.ReportPath + ".settings.about.narrow.dark.png");
                    aboutPage.ChangeView(null, aboutPage.ScrollableHeight, null, true);
                    await Task.Delay(100, timeout.Token);
                    await CaptureAsync(root, Program.ReportPath + ".settings.about.diagnostics.png");
                    aboutPage.ChangeView(null, 0, null, true);
                    root.Width = double.NaN;
                    root.Height = double.NaN;
                    root.ColumnDefinitions[0].Width = new GridLength(260);
                    settingsPage.UpdateResponsiveLayout(30);
                    root.RequestedTheme = ElementTheme.Light;
                    root.Background = new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke);
                    Program.Results.Add(new { control = "About project links, version and narrow dark layout", passed = failures.Count == 0 });
                }
            }
            typeof(SettingsPage).GetMethod("ShowCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settingsPage, new object[] { "shortcuts" });
            root.UpdateLayout();
            await Task.Delay(300, timeout.Token);
            var shortcutCards = ((StackPanel)settingsPage.FindName("ShortcutOptions")).Children.OfType<SettingsCard>().ToArray();
            if (shortcutCards.Length != 5 || shortcutCards.Any(c => c.Content is not ShortcutKeyPicker)) failures.Add("All shortcuts must use ShortcutKeyPicker.");
            var keyPicker = (ShortcutKeyPicker)shortcutCards[0].Content;
            if (keyPicker.Content is not ShortcutKeyPanel keyPanel || keyPanel.Children.Count != 3) failures.Add("ShortcutKeyPanel must render three key caps.");
            await CaptureAsync(root, Program.ReportPath + ".shortcuts.png");
            var keyPeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(keyPicker);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)keyPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(250, timeout.Token);
            var keyDialog = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(popup => new[] { popup.Child }.Concat(Descendants(popup.Child))).OfType<ContentDialog>().Single();
            if (keyDialog.XamlRoot != root.XamlRoot) failures.Add("Shortcut picker dialog must use the current XamlRoot.");
            typeof(ShortcutKeyPicker).GetMethod("RecordKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(keyPicker,
                new object[] { Windows.System.VirtualKey.K, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift });
            keyDialog.Hide();
            while (Field<bool>(keyPicker, "_isOpen")) await Task.Delay(50, timeout.Token);
            if (keyPicker.Key != "T") failures.Add("Cancelling shortcut recording must preserve the current key.");
            var shortcutUpdates = 0;
            settingsPage.SettingsChanged += (_, change) => { if (change.Preferences is not null) shortcutUpdates++; };
            async Task RecordAndSave(string key)
            {
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)keyPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await Task.Delay(200, timeout.Token);
                typeof(ShortcutKeyPicker).GetMethod("RecordKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(keyPicker,
                    new object[] { Enum.Parse<Windows.System.VirtualKey>(key), Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift });
                var open = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                    .SelectMany(p => new[] { p.Child }.Concat(Descendants(p.Child))).OfType<ContentDialog>().Single();
                var save = Descendants(open).OfType<Button>().Single(b => b.Name == "PrimaryButton");
                var savePeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(save);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)savePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                while (Field<bool>(keyPicker, "_isOpen")) await Task.Delay(50, timeout.Token);
            }
            await RecordAndSave("K");
            if (keyPicker.Key != "K" || shortcutUpdates != 1) failures.Add("Recorded shortcut must be saved once.");
            await RecordAndSave("W");
            if (keyPicker.Key != "K" || shortcutUpdates != 1 || ((TextBlock)settingsPage.FindName("ShortcutError")).Visibility != Visibility.Visible)
                failures.Add("Duplicate shortcut must show an error and retain the previous binding.");
            Program.Results.Add(new { control = "breadcrumb and shortcut controls", passed = failures.Count == 0 });
            ReturnToSettingsHome(settingsPage, root, "shortcuts", failures);
            ((Expander)settingsPage.FindName("LightColorsExpander")).IsExpanded = true;
            var fields = Field<Dictionary<string, Button>>(settingsPage, "_lightColors");
            var updates = 0;
            settingsPage.SettingsChanged += (_, update) =>
            {
                if (update.TerminalColors is not null) { updates++; workspace.SetTerminalColors(update.TerminalColors); }
            };
            if (((ScrollViewer)settingsPage.FindName("TerminalColorsPage")).Visibility != Visibility.Collapsed)
                failures.Add("Color editor must be a secondary settings page.");
            typeof(SettingsPage).GetMethod("OpenTerminalColors_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settingsPage, new object[] { settingsPage, new RoutedEventArgs() });
            root.UpdateLayout();
            await Task.Delay(300, timeout.Token);
            await CaptureAsync(root, Program.ReportPath + ".terminal.png");
            if (((ScrollViewer)settingsPage.FindName("SettingsHome")).Visibility != Visibility.Collapsed)
                failures.Add("Opening the color editor must hide settings home.");
            await Task.Delay(150, timeout.Token);
            // Open the real picker through its settings button; cancelling must not change the draft.
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(fields["background"]);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(250, timeout.Token);
            var dialog = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(popup => new[] { popup.Child }.Concat(Descendants(popup.Child))).OfType<ColorDialog>().Single();
            if (dialog.XamlRoot != settingsPage.XamlRoot) failures.Add("ColorDialog must be parented to the settings window.");
            dialog.Hide();
            while (Field<bool>(settingsPage, "_colorDialogOpen")) await Task.Delay(50, timeout.Token);
            if (fields["background"].Tag is not null) failures.Add("Cancelling the color picker must retain the draft.");
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(250, timeout.Token);
            dialog = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(popup => new[] { popup.Child }.Concat(Descendants(popup.Child))).OfType<ColorDialog>().Single();
            ((ColorPicker)((ScrollViewer)dialog.Content).Content).Color = ColorDialog.Parse("#123456");
            var confirm = Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton");
            var confirmPeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(confirm);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)confirmPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            while (Field<bool>(settingsPage, "_colorDialogOpen")) await Task.Delay(50, timeout.Token);
            if (fields["background"].Tag as string != "#123456") failures.Add("Confirming ColorDialog must update the selected color.");
            fields["red"].Tag = "#ABCDEF";
            typeof(SettingsPage).GetMethod("SaveColors_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settingsPage, new object[] { settingsPage, new RoutedEventArgs() });
            ReturnToSettingsHome(settingsPage, root, "terminal", failures);
            if (((ScrollViewer)settingsPage.FindName("SettingsHome")).Visibility != Visibility.Visible)
                failures.Add("Back must return to settings home.");
            // Page scrollers reach the viewport edge; content retains its responsive inset.
            foreach (var inset in new[] { 16d, 30d })
            {
                settingsPage.UpdateResponsiveLayout(inset);
                foreach (var name in new[] { "SettingsHome", "AppearancePage", "TerminalColorsPage", "ConnectionPage", "TransferSettingsPage", "ShortcutSettingsPage", "DataSettingsPage", "AboutSettingsPage" })
                {
                    var scroller = (ScrollViewer)settingsPage.FindName(name);
                    scroller.Visibility = Visibility.Visible;
                    scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                    root.UpdateLayout();
                    CheckScrollEdge(scroller, root, failures, name);
                    if (Math.Abs(((FrameworkElement)scroller.Content).Margin.Right - inset) > 0.1) failures.Add(name + " lost content inset.");
                    scroller.Visibility = Visibility.Collapsed;
                }
            }
            root.Children.Remove(settingsPage);
            var overview = new OverviewPage();
            var catalog = new ServerCatalogPage(WinRT.Interop.WindowNative.GetWindowHandle(_window), _ => false);
            catalog.SetProfiles(Enumerable.Range(1, 30).Select(i => new ServerProfile { Name = "Fixture " + i, Host = "offline.invalid", Username = "test" }).ToArray());
            foreach (var page in new UserControl[] { overview, catalog })
            {
                Grid.SetColumn(page, 1);
                root.Children.Add(page);
                foreach (var inset in new[] { 16d, 30d })
                {
                    overview.UpdateResponsiveLayout(inset);
                    catalog.UpdateResponsiveLayout(inset);
                    root.UpdateLayout();
                    var scroller = page == overview ? (ScrollViewer)overview.FindName("RootScrollViewer") : Descendants((ListView)catalog.FindName("ProfilesList")).OfType<ScrollViewer>().First();
                    scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                    root.UpdateLayout();
                    CheckScrollEdge(scroller, root, failures, page.GetType().Name);
                }
                await Task.Delay(300, timeout.Token);
                root.UpdateLayout();
                await CaptureAsync(root, Program.ReportPath + "." + page.GetType().Name + ".png");
                root.Children.Remove(page);
            }
            Program.Results.Add(new { control = "page scrollbars at viewport edge", passed = failures.Count == 0 });
            root.Children.Add(workspace);
            await Task.Delay(400, timeout.Token);
            var customBackground = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor"));
            if (updates != 1 || customBackground != "rgb(18, 52, 86)") failures.Add("Saved custom background did not reach the cached terminal.");
            typeof(SettingsPage).GetMethod("ResetColors_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settingsPage, new object[] { settingsPage, new RoutedEventArgs() });
            await Task.Delay(200, timeout.Token);
            var resetBackground = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor"));
            if (updates != 2 || resetBackground != "rgb(254, 254, 254)") failures.Add("Reset did not restore the terminal's default background.");
            Program.Results.Add(new { control = "terminal color settings", customBackground, resetBackground, updates, passed = failures.Count == 0 });
            await workspace.DisposeAsync();
            root.Children.Remove(workspace);
            foreach (var protocol in new[] { ConnectionProtocol.Ftp, ConnectionProtocol.Sftp })
            {
                var fileProfile = new ServerProfile { Protocol = protocol, Name = "Offline files", Host = "offline.invalid", Username = "fixture" };
                await using var filesWorkspace = new SessionWorkspace(fileProfile, WinRT.Interop.WindowNative.GetWindowHandle(_window),
                    (_, _) => Task.FromResult<ISshConnection?>(null), _ => Task.FromResult(false), () => Task.FromResult<string?>(null));
                Grid.SetColumn(filesWorkspace, 1);
                root.Children.Add(filesWorkspace);
                root.UpdateLayout();
                filesWorkspace.ExecuteShortcut("files");
                if (filesWorkspace.Content is not SftpWorkspaceView || Descendants(filesWorkspace).OfType<TerminalPane>().Any())
                    failures.Add(protocol + " must show files without a terminal.");
                sidebar.UpdateSession(fileProfile, SessionConnectionState.Connected);
                if (((StackPanel)sidebar.FindName("MetricsSection")).Visibility != Visibility.Collapsed)
                    failures.Add(protocol + " must hide SSH metrics.");
                await CaptureAsync(root, Program.ReportPath + "." + protocol + ".png");
                root.Children.Remove(filesWorkspace);
            }
            var testAttempt = 0;
            var canceledAttempt = 0;
            var editedProfile = new ServerProfile { Name = "测试服务器", Host = "offline.invalid", Username = "fixture" };
            var profileWindow = new ServerProfileWindow(editedProfile, new ServerProfileWindowContext
            {
                OwnerXamlRoot = root.XamlRoot, OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window),
                HasSavedCredential = false, ExistingProfiles = [],
                TestConnectionAsync = async (draft, secret, confirm, token) =>
                {
                    if (ReferenceEquals(draft, editedProfile) || draft.Host != "changed.invalid")
                        throw new InvalidOperationException("Test must use a detached current draft.");
                    testAttempt++;
                    if (testAttempt == 1)
                    {
                        if (!await Task.Run(() => confirm(new HostFingerprintRequiredEventArgs
                        { Profile = draft, Fingerprint = "SYNTHETIC-FINGERPRINT", KeyType = "fixture" }, token)))
                            throw new IOException("Trust rejected");
                    }
                    else if (testAttempt == 2) throw new IOException("离线模拟连接失败");
                    else
                    {
                        try { await Task.Delay(Timeout.Infinite, token); }
                        finally { if (token.IsCancellationRequested) canceledAttempt = testAttempt; }
                    }
                }
            });
            profileWindow.Activate();
            await Task.Delay(250);
            var profileRoot = (Grid)profileWindow.Content;
            var formScroller = Descendants(profileRoot).OfType<ScrollViewer>().First();
            var form = (StackPanel)formScroller.Content;
            if (WinRT.Interop.WindowNative.GetWindowHandle(profileWindow) == WinRT.Interop.WindowNative.GetWindowHandle(_window))
                failures.Add("Profile editor must have a separate top-level window.");
            var protocolBox = form.Children.OfType<ComboBox>().Single(box => (string)box.Header == "连接协议");
            var portBox = form.Children.OfType<NumberBox>().Single();
            var authenticationBox = Descendants(form).OfType<ComboBox>().Single(box => (string)box.Header == "认证方式");
            var secretBox = Descendants(form).OfType<PasswordBox>().Single();
            var keyPathBox = Descendants(form).OfType<TextBox>().Single(box => box.Header as string == "私钥文件");
            formScroller.ChangeView(null, formScroller.ScrollableHeight, null, disableAnimation: true);
            profileRoot.UpdateLayout();
            double Top(FrameworkElement element) => element.TransformToVisual(profileRoot).TransformPoint(new Windows.Foundation.Point()).Y;
            if (Math.Abs(Top(authenticationBox) - Top(secretBox)) > 1)
                failures.Add("Password input must share a row with authentication selection.");
            await CaptureAsync(profileRoot, Program.ReportPath + ".ProfileWindow.PasswordRow.png");
            authenticationBox.SelectedIndex = 1;
            profileRoot.UpdateLayout();
            if (Math.Abs(Top(authenticationBox) - Top(keyPathBox)) > 1 || Top(secretBox) <= Top(keyPathBox))
                failures.Add("Key path must share the authentication row, with passphrase below.");
            await CaptureAsync(profileRoot, Program.ReportPath + ".ProfileWindow.PrivateKeyRow.png");
            authenticationBox.SelectedIndex = 0;
            formScroller.ChangeView(null, 0, null, disableAnimation: true);
            profileRoot.UpdateLayout();
            protocolBox.SelectedIndex = 2;
            if (portBox.Value != 21 || authenticationBox.IsEnabled) failures.Add("FTP form defaults incorrect.");
            protocolBox.SelectedIndex = 1;
            if (portBox.Value != 22 || !authenticationBox.IsEnabled) failures.Add("SFTP form defaults incorrect.");
            portBox.Value = 2121;
            protocolBox.SelectedIndex = 2;
            if (portBox.Value != 2121) failures.Add("Protocol switch overwrote custom port.");
            protocolBox.SelectedIndex = 0;
            form.Children.OfType<TextBox>().Single(box => (string)box.Header == "主机地址").Text = "changed.invalid";
            var testButton = Descendants(profileRoot).OfType<Button>().Single(button => button.Content as string == "测试连接");
            var saveButton = Descendants(profileRoot).OfType<Button>().Single(button => button.Content as string == "保存修改");
            var saveAndConnectButton = Descendants(profileRoot).OfType<Button>().Single(button => button.Content as string == "保存并连接");
            var testInfoBar = Descendants(profileRoot).OfType<InfoBar>().Single();
            if (Descendants(profileRoot).OfType<Button>().Any(button => button.Name == "CancelButton" || button.Content as string == "取消"))
                failures.Add("Profile editor must not have a footer cancel action.");
            profileRoot.UpdateLayout();
            var initialActionY = testButton.TransformToVisual(profileRoot).TransformPoint(new Windows.Foundation.Point()).Y;
            var initialFormHeight = formScroller.ActualHeight;
            void Click(Button button) => ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)
                new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button)
                    .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            async Task WaitFor(Func<bool> condition, int timeoutSeconds = 5)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                while (!condition()) await Task.Delay(30, wait.Token);
            }
            Click(testButton);
            await WaitFor(() => Descendants(profileRoot).OfType<TextBlock>().Any(block => block.Text.Contains("SYNTHETIC-FINGERPRINT")));
            if (testButton.Content is not ProgressRing { IsActive: true } || testInfoBar.IsOpen)
                failures.Add("Test action must show an active progress ring without a result notification while running.");
            if (saveButton.IsEnabled || saveAndConnectButton.IsEnabled)
                failures.Add("Save actions must be disabled during a connection test.");
            Click(Descendants(profileRoot).OfType<Button>().Single(button => button.Content as string == "信任并测试"));
            await WaitFor(() => testButton.Content as string == "测试连接");
            if (!testInfoBar.IsOpen || testInfoBar.Severity != InfoBarSeverity.Success)
                failures.Add("Connection test success is missing.");
            profileRoot.UpdateLayout();
            var toastBounds = testInfoBar.TransformToVisual(profileRoot).TransformBounds(new Windows.Foundation.Rect(0, 0, testInfoBar.ActualWidth, testInfoBar.ActualHeight));
            var actionY = testButton.TransformToVisual(profileRoot).TransformPoint(new Windows.Foundation.Point()).Y;
            var currentFormHeight = formScroller.ActualHeight;
            if (Math.Abs(initialActionY - actionY) > 1 || Math.Abs(initialFormHeight - currentFormHeight) > 1 || toastBounds.Top > 80 || toastBounds.Right > profileRoot.ActualWidth)
                failures.Add("Test result must float at the top without moving form actions.");
            await CaptureAsync(profileRoot, Program.ReportPath + ".ProfileWindow.Success.png");
            await WaitFor(() => !testInfoBar.IsOpen, 8);
            if (editedProfile.Host != "offline.invalid") failures.Add("Test mutated saved profile.");
            Click(testButton);
            await WaitFor(() => testAttempt == 2 && testButton.Content as string == "测试连接");
            if (!testInfoBar.IsOpen || testInfoBar.Severity != InfoBarSeverity.Error || testInfoBar.Message != "离线模拟连接失败")
                failures.Add("Connection test failure is missing.");
            profileRoot.UpdateLayout();
            var toastCloseButton = Descendants(testInfoBar).OfType<Button>().Single(button => button.Name == "CloseButton");
            if (toastCloseButton.Background is not SolidColorBrush { Color.A: 0 } || toastCloseButton.BorderThickness != new Thickness(0))
                failures.Add("Toast close button must have a transparent background and no border.");
            void VerifyToastCloseButtonTheme()
            {
                var toastMessage = Descendants(testInfoBar).OfType<TextBlock>().Single(block => block.Name == "Message");
                var toastCloseGlyph = Descendants(toastCloseButton).OfType<SymbolIcon>().Single();
                if (toastMessage.Foreground is not SolidColorBrush messageForeground
                    || toastCloseButton.Foreground is not SolidColorBrush closeForeground
                    || toastCloseGlyph.Foreground is not SolidColorBrush glyphForeground
                    || closeForeground.Color != messageForeground.Color || glyphForeground.Color != messageForeground.Color)
                    failures.Add("Toast close glyph must follow the notification text theme.");
            }
            VerifyToastCloseButtonTheme();
            Click(toastCloseButton);
            if (testInfoBar.IsOpen) failures.Add("Toast close action did not dismiss the notification.");
            Click(testButton);
            await WaitFor(() => testAttempt == 3);
            Click(testButton);
            await WaitFor(() => testButton.Content as string == "测试连接");
            if (!saveButton.IsEnabled) failures.Add("Save action did not recover after cancellation.");
            if (!testInfoBar.IsOpen || testInfoBar.Severity != InfoBarSeverity.Informational)
                failures.Add("Connection test cancellation is missing.");
            profileRoot.UpdateLayout();
            await CaptureAsync(profileRoot, Program.ReportPath + ".ProfileWindow.png");
            root.RequestedTheme = ElementTheme.Dark;
            await WaitFor(() => profileRoot.ActualTheme == ElementTheme.Dark);
            profileWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(560 * root.XamlRoot.RasterizationScale), (int)(560 * root.XamlRoot.RasterizationScale)));
            await Task.Delay(100);
            profileRoot.UpdateLayout();
            VerifyToastCloseButtonTheme();
            foreach (var button in new[] { testButton, saveButton, saveAndConnectButton })
            {
                var bounds = button.TransformToVisual(profileRoot).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                if (bounds.Bottom > profileRoot.ActualHeight || bounds.Right > profileRoot.ActualWidth || bounds.Top < 0)
                    failures.Add("Profile action is outside the resized window.");
            }
            await CaptureAsync(profileRoot, Program.ReportPath + ".ProfileWindow.NarrowDark.png");
            Click(testButton);
            await WaitFor(() => testAttempt == 4);
            profileWindow.Close();
            await WaitFor(() => canceledAttempt == 4);
            if (await profileWindow.Completion is not null) failures.Add("Closing editor must cancel unsaved changes.");
            if (editedProfile.Host != "offline.invalid") failures.Add("Closing editor mutated saved profile.");
            root.RequestedTheme = ElementTheme.Light;
            foreach (var connectAfterSave in new[] { false, true })
            {
                var savedProfile = connectAfterSave ? null : new ServerProfile { Name = "原名称", Host = "offline.invalid", Username = "fixture" };
                var editor = new ServerProfileWindow(savedProfile, new ServerProfileWindowContext
                {
                    OwnerXamlRoot = root.XamlRoot, OwnerWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window),
                    HasSavedCredential = false, ExistingProfiles = []
                });
                editor.Activate();
                await Task.Delay(100);
                var editorRoot = (Grid)editor.Content;
                Button Action(string label) => Descendants(editorRoot).OfType<Button>().Single(button => button.Content as string == label);
                var save = Action(connectAfterSave ? "保存并连接" : "保存修改");
                var editorForm = (StackPanel)Descendants(editorRoot).OfType<ScrollViewer>().First().Content;
                if (connectAfterSave)
                {
                    Click(save);
                    await WaitFor(() => Descendants(editorRoot).OfType<TextBlock>().Any(block => block.Text.Contains("不能为空")));
                    if (editor.Completion.IsCompleted) failures.Add("Invalid profile closed the editor.");
                }
                editorForm.Children.OfType<TextBox>().Single(box => (string)box.Header == "显示名称").Text = "已修改";
                editorForm.Children.OfType<TextBox>().Single(box => (string)box.Header == "主机地址").Text = "saved.invalid";
                Descendants(editorForm).OfType<TextBox>().Single(box => box.Header as string == "用户名").Text = "fixture";
                Click(save);
                var result = await editor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                if (result is null || result.Profile.Name != "已修改" || result.Profile.Host != "saved.invalid" || result.ConnectAfterSave != connectAfterSave)
                    failures.Add("Profile editor save result was incorrect.");
            }
            Program.Results.Add(new { control = "FTP/SFTP workspaces and independent profile editor", passed = failures.Count == 0 });
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            Program.Finish();
        }
        catch (Exception e) { Program.Finish(e); }
    }
    private static void ReturnToSettingsHome(SettingsPage page, Grid root, string category, List<string> failures)
    {
        root.UpdateLayout();
        var breadcrumb = (BreadcrumbBar)page.FindName("SettingsBreadcrumb");
        var home = Descendants(breadcrumb).OfType<BreadcrumbBarItem>().Single(item => item.Content as string == "设置");
        new Microsoft.UI.Xaml.Automation.Peers.BreadcrumbBarItemAutomationPeer(home).Invoke();
        root.UpdateLayout();
        if (((ScrollViewer)page.FindName("SettingsHome")).Visibility != Visibility.Visible
            || breadcrumb.ItemsSource is not string[] { Length: 1 })
            failures.Add("Invoking the root breadcrumb must return to settings home.");
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
        var homePanel = (StackPanel)((ScrollViewer)page.FindName("SettingsHome")).Content;
        FrameworkElement expectedCard = category == "terminal" ? (FrameworkElement)page.FindName("OpenTerminalColorsButton")
            : homePanel.Children.OfType<SettingsCard>().Single(card => card.Tag as string == category);
        if (focused != expectedCard && !Descendants(expectedCard).Contains(focused))
            failures.Add("Breadcrumb return must restore focus to the originating category card.");
        Program.Results.Add(new { control = "root breadcrumb return and focus", passed = failures.Count == 0 });
    }

    private static async Task VerifyTransferAndFileFlyoutsAsync(Grid root, SftpWorkspaceView sftp, List<string> failures)
    {
        var center = new TransferCenter();
        var task = center.Add(Guid.NewGuid(), "Offline server", "上传", "file.bin", "/fixture", () => Task.CompletedTask, () => true);
        void Publish(long bytes)
        {
            var item = new TransferQueueItem("folder/file.bin", 100, TransferItemState.Transferring, bytes);
            var snapshot = new SftpSessionSnapshot(SftpSessionState.Idle, SftpDirectoryListing.Empty("/"), true, true, false, "", null)
            {
                Transfer = new(SftpTransferState.Transferring, "Offline transfer", new(bytes, 100, 512)),
                Queue = new([item], 1, 0, 0, 0)
            };
            typeof(TransferTask).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, [snapshot]);
        }
        var anchor = new Button { Content = "Offline flyout fixture", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(anchor, 1);
        root.Children.Add(anchor);
        var view = new TransferCenterView { Width = 400, Height = 500 };
        view.SetCenter(center);
        var flyout = new Flyout { Content = view };
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            Publish(25);
            flyout.ShowAt(anchor);
            await Task.Delay(200);
            view.UpdateLayout();
            var expander = Descendants(view).OfType<Expander>().Single();
            expander.IsExpanded = true;
            await Task.Delay(150);
            if (!Descendants(view).OfType<TextBlock>().Any(text => text.Text == "25%")) failures.Add("File detail must display its initial percentage.");
            Publish(50);
            await Task.Delay(100);
            if (view.ActualTheme != theme || !Descendants(view).OfType<TextBlock>().Any(text => text.Text == "50%"))
                failures.Add("Transfer flyout must follow the theme and update the bound status label.");
            // RenderTargetBitmap cannot reliably capture native popup composition;
            // verify the live popup's theme, layout and bound text instead.
            if (view.ActualWidth <= 0 || view.ActualHeight <= 0) failures.Add("Transfer flyout must have a visible layout.");
            await HideFlyoutAsync(flyout);

            var menu = (MenuFlyout)typeof(SftpWorkspaceView).GetMethod("BuildEmptyAreaMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sftp, null)!;
            menu.ShowAt(anchor);
            await Task.Delay(150);
            var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Select(p => p.Child).OfType<FrameworkElement>().Single();
            if (presenter.ActualTheme != theme || !Descendants(presenter).OfType<MenuFlyoutItem>().Any())
                failures.Add("File context menu must retain its native items and theme.");
            if (presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0) failures.Add("File menu must have a visible layout.");
            await HideFlyoutAsync(menu);
            Program.Results.Add(new { control = "transfer detail updates and file menu", theme = theme.ToString(), passed = failures.Count == 0 });
        }
        root.RequestedTheme = ElementTheme.Light;
        root.Children.Remove(anchor);
    }

    private static async Task HideFlyoutAsync(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, object args) => closed.TrySetResult();
        flyout.Closed += OnClosed;
        flyout.Hide();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        flyout.Closed -= OnClosed;
    }

    private static async Task VerifyTerminalAsync(TerminalPane terminal, WebView2 web, List<string> failures)
    {
        var input = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInput(object? sender, string data) => input.TrySetResult(data);
        var resize = new TaskCompletionSource<TerminalResizeRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResize(object? sender, TerminalResizeRequestedEventArgs args) => resize.TrySetResult(args);
        terminal.InputReceived += OnInput;
        terminal.ResizeRequested += OnResize;
        try
        {
            web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"paste\",\"data\":\"offline-input\"}");
            if (await input.Task.WaitAsync(TimeSpan.FromSeconds(3)) != "offline-input")
                failures.Add("xterm input must reach the host intact.");
            terminal.SetFontSize(18);
            var size = await resize.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (size.Columns <= 0 || size.Rows <= 0) failures.Add("Font resizing must preserve positive terminal dimensions.");
            terminal.Search();
            await Task.Delay(150);
            var result = await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({searchVisible:!document.getElementById('search').hidden,fontSize:getComputedStyle(document.querySelector('.xterm-rows')).fontSize,hasOutput:document.querySelector('.xterm-rows').textContent.includes('Offline output')})");
            using var state = JsonDocument.Parse(JsonSerializer.Deserialize<string>(result)!);
            Program.Results.Add(new { control = "terminal bridge state", state = state.RootElement.Clone() });
            if (!state.RootElement.GetProperty("searchVisible").GetBoolean() || !state.RootElement.GetProperty("hasOutput").GetBoolean()
                || state.RootElement.GetProperty("fontSize").GetString() != "18px")
                failures.Add("Terminal output, font size and search must survive asset cleanup.");
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('search-input').value='Offline output';document.getElementById('search-next').click();document.getElementById('search-close').click()");
            terminal.SetFontSize(14);
            Program.Results.Add(new { control = "terminal output, input, font resizing and search", passed = failures.Count == 0 });
        }
        finally { terminal.InputReceived -= OnInput; terminal.ResizeRequested -= OnResize; }
    }

    private static async Task VerifyTextFlyoutAsync(Grid root, List<string> failures)
    {
        var text = new TextBox { Width = 240, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(text, 1);
        root.Children.Add(text);
        root.UpdateLayout();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            await Task.Delay(150);
            text.Text = "Offline text fixture";
            text.Focus(FocusState.Programmatic);
            text.SelectAll();
            var flyout = text.ContextFlyout;
            if (flyout is not TextCommandBarFlyout) throw new InvalidOperationException("Expected the native text context flyout.");
            flyout.ShowAt(text);
            await Task.Delay(150);
            var buttons = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(p => Descendants(p.Child)).OfType<AppBarButton>().ToArray();
            if (buttons.Length == 0) failures.Add("Text context flyout must create native command buttons.");
            foreach (var button in buttons)
            {
                var buttonStates = new Dictionary<string, bool>();
                foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Disabled" })
                {
                    buttonStates[state] = VisualStateManager.GoToState(button, state, false);
                    if (!buttonStates[state]) failures.Add("Text command button lost visual state: " + state);
                }
                Program.Results.Add(new { control = "text command button", label = button.Label, width = button.ActualWidth, foreground = button.Foreground?.GetType().Name, states = buttonStates });
                if (button.Foreground is not SolidColorBrush) failures.Add("Text command button must retain its foreground.");
            }
            Program.Results.Add(new { control = "native text command flyout states", theme = theme.ToString(), buttons = buttons.Length, passed = failures.Count == 0 });
            await HideFlyoutAsync(flyout);
        }
        root.RequestedTheme = ElementTheme.Light;
        root.Children.Remove(text);
    }

    private static async Task VerifyIconEdgesAsync(SettingsPage settingsPage, Grid root, string iconName, double scale, List<string> failures)
    {
        var icon = new PathIcon
        {
            Data = IconGeometryConverter.Parse((string)settingsPage.Resources[$"Settings{iconName}IconData"]),
            Style = (Style)settingsPage.Resources["SettingsOptionIconStyle"],
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black)
        };
        var size = 20 * scale;
        var tile = new Grid
        {
            Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        tile.Children.Add(new Viewbox { Child = icon, Stretch = Stretch.Uniform });
        Grid.SetColumn(tile, 1);
        root.Children.Add(tile);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(tile, (int)size, (int)size);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var edgeIsClear = true;
        var hasArtwork = false;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var painted = pixels[offset] < 255 || pixels[offset + 1] < 255 || pixels[offset + 2] < 255;
                hasArtwork |= painted;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1) edgeIsClear &= !painted;
            }
        }
        var passed = edgeIsClear && hasArtwork;
        if (!passed) failures.Add($"{iconName} at {scale} must draw a visible icon with clear edge pixels.");
        await CaptureAsync(tile, Program.ReportPath + $".icon.{iconName}.{(int)(scale * 100)}.png");
        ((Viewbox)tile.Children[0]).Child = new PathIcon
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry),
                (string)settingsPage.Resources[$"Settings{iconName}IconData"]),
            Style = (Style)settingsPage.Resources["SettingsOptionIconStyle"],
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black)
        };
        root.UpdateLayout();
        await bitmap.RenderAsync(tile, (int)size, (int)size);
        var tightPixels = (await bitmap.GetPixelsAsync()).ToArray();
        var changedPixels = Enumerable.Range(0, width * height).Count(i =>
            pixels[i * 4] != tightPixels[i * 4] || pixels[i * 4 + 1] != tightPixels[i * 4 + 1] || pixels[i * 4 + 2] != tightPixels[i * 4 + 2]);
        Program.Results.Add(new { control = "settings icon edge coverage", iconName, scale, edgeIsClear, hasArtwork, changedPixels, passed });
        root.Children.Remove(tile);
    }

    private static void CheckScrollEdge(ScrollViewer scroller, FrameworkElement root, List<string> failures, string name)
    {
        var bar = Descendants(scroller).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
            .First(b =>
            {
                if (b.Orientation != Orientation.Vertical) return false;
                DependencyObject parent = b;
                do { parent = VisualTreeHelper.GetParent(parent); } while (parent is not null && parent is not ScrollViewer);
                return ReferenceEquals(parent, scroller);
            });
        var right = bar.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(bar.ActualWidth, 0)).X;
        if (Math.Abs(right - root.ActualWidth) > 2) failures.Add($"{name} scrollbar ends at {right}, expected {root.ActualWidth}.");
    }

    private async Task VerifySftpDeleteAsync()
    {
        _window = new Window { Title = "FluentShell offline deletion regression" };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));
        var root = new Grid { RequestedTheme = ElementTheme.Light };
        var view = new SftpWorkspaceView(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        root.Children.Add(view);
        _window.Content = root;
        _window.Activate();
        var folder = new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true };
        var listing = new SftpDirectoryListing("/", [folder]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            view.Render(new SftpSessionSnapshot(SftpSessionState.Deleting, listing, false, false, true,
                "正在删除“Mod”…", null));
            await Task.Delay(200, timeout.Token);
            root.UpdateLayout();
            if (((Grid)view.FindName("DirectoryLoadingOverlay")).Visibility != Visibility.Visible ||
                ((TextBlock)view.FindName("DirectoryLoadingText")).Text != "正在删除…" ||
                ((TextBox)view.FindName("PathBox")).IsEnabled)
                throw new InvalidOperationException("Deletion must show a busy indicator and disable browsing.");
            await CaptureAsync(root, Program.ReportPath + $".busy.{theme}.png");
            view.Render(new SftpSessionSnapshot(SftpSessionState.Idle, listing, true, true, true, "删除完成。", null));
            if (((Grid)view.FindName("DirectoryLoadingOverlay")).Visibility != Visibility.Collapsed ||
                !((TextBox)view.FindName("PathBox")).IsEnabled)
                throw new InvalidOperationException("Deletion completion must restore browsing.");

            foreach (var link in new[] { false, true })
            {
                var item = link
                    ? new RemoteFileItem { Name = "link", FullPath = "/link", IsDirectory = true, IsSymbolicLink = true }
                    : folder;
                var confirmation = view.ConfirmDeleteAsync(item);
                await Task.Delay(200, timeout.Token);
                var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                    .SelectMany(p => new[] { p.Child }.Concat(Descendants(p.Child))).OfType<ContentDialog>().Single();
                var content = (string)dialog.Content;
                if (!content.Contains(link ? "链接指向的内容不会被删除" : "及其全部内容") ||
                    dialog.DefaultButton != ContentDialogButton.Close || dialog.ActualWidth <= 0 ||
                    dialog.ActualTheme != view.ActualTheme)
                    throw new InvalidOperationException("Delete confirmation must explain its scope, default to cancel and follow the theme.");
                Program.Results.Add(new { control = "SFTP delete confirmation", theme = theme.ToString(), link, content,
                    width = dialog.ActualWidth, height = dialog.ActualHeight, passed = true });
                await CaptureAsync(dialog, Program.ReportPath + $".dialog.{theme}.{link}.png");
                dialog.Hide();
                if (await confirmation.WaitAsync(timeout.Token))
                    throw new InvalidOperationException("Cancelling delete confirmation must return false.");
            }
            Program.Results.Add(new { control = "SFTP deletion busy state and completion", theme = theme.ToString(), passed = true });
        }
        _window.Close();
        Program.Finish();
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static async Task CaptureAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        var framework = (FrameworkElement)element;
        await bitmap.RenderAsync(element, (int)Math.Ceiling(framework.ActualWidth), (int)Math.Ceiling(framework.ActualHeight));
        var pixels = await bitmap.GetPixelsAsync();
        using var file = File.Create(path);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
