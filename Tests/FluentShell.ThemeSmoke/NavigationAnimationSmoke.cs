using System.Reflection;
using System.Text.Json;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifyNavigationAnimationAsync()
    {
        var window = new MainWindow { Title = "FluentShell · DrillIn 动效预览（离线）" };
        _window = window;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Prevent normal activation from loading saved profiles or preferences.
        typeof(MainWindow).GetField("_loaded", flags)!.SetValue(window, true);
        var root = (Grid)window.Content;
        var pages = (ContentPresenter)root.FindName("PageContentPresenter");
        var sessions = (ContentPresenter)root.FindName("SessionContentPresenter");
        var host = Field<SessionHost>(window, "_sessionHost");
        var shell = Field<ShellCoordinator>(window, "_shell");
        var coordinator = Field<SessionCoordinator<IShellSession>>(shell, "_sessions");
        var strip = Field<SessionTabStrip>(window, "_sessionTabStrip");
        var add = (Button)strip.FindName("NewSessionButton");
        void Navigate(string page) => typeof(MainWindow).GetMethod("NavigateTo", flags)!.Invoke(window, [page, false]);
        void NewTab() => ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
        Navigate("overview");
        window.Activate();
        await Task.Delay(400);

        var profile = new ServerProfile { Name = "动效预览", Host = "offline.invalid", Username = "fixture" };
        SessionWorkspace CreateSession() => new(profile, WinRT.Interop.WindowNative.GetWindowHandle(window),
            (_, _) => Task.FromResult<ISshConnection?>(null), _ => Task.FromResult(false), () => Task.FromResult<string?>(null));
        var first = CreateSession();
        var second = CreateSession();
        foreach (var session in new[] { first, second })
        {
            coordinator.Add(session);
            host.Add(session);
        }
        shell.SelectSession(first);
        await Task.Delay(600);

        var animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            var navigation = (NavigationView)root.FindName("RootNavigationView");
            var paneWasOpen = navigation.IsPaneOpen;
            var paneToggle = (Button)root.FindName("PaneToggleButton");
            foreach (var open in new[] { true, false })
            {
                navigation.IsPaneOpen = open;
                await Task.Delay(250);
                root.UpdateLayout();
                await CaptureAsync(paneToggle, Program.ReportPath + $".{theme}.panel.{(open ? "open" : "closed")}.png");
            }
            navigation.IsPaneOpen = paneWasOpen;
            await Task.Delay(250);
            shell.SelectSession(first);
            await Task.Delay(400);
            NewTab();
            await Task.Delay(60);
            var scale = (CompositeTransform)pages.RenderTransform;
            var midScale = scale.ScaleX;
            var midOpacity = pages.Opacity;
            if (pages.Visibility != Visibility.Visible || sessions.Visibility != Visibility.Collapsed ||
                host.Selected is not null || ((StackPanel)strip.FindName("TabPanel")).Children.Count != 2)
                throw new InvalidOperationException("The add button must reveal the new-tab page and preserve existing sessions.");
            if (animationsEnabled && !(midScale > 0.91 && midScale < 1 && midOpacity > 0 && midOpacity < 1))
                throw new InvalidOperationException($"New-tab entrance did not animate: scale={midScale}, opacity={midOpacity}.");
            await Task.Delay(400);
            AssertRestored(pages);

            shell.SelectSession(first);
            await Task.Delay(50);
            shell.SelectSession(second);
            await Task.Delay(50);
            NewTab();
            await Task.Delay(50);
            shell.SelectSession(first);
            await Task.Delay(400);
            AssertRestored(pages);
            AssertRestored(sessions);
            if (!ReferenceEquals(sessions.Content, first) || !coordinator.Contains(second))
                throw new InvalidOperationException("Rapid switching must retain the selected workspace and other sessions.");

            NewTab();
            await Task.Delay(400);
            navigation.SelectedItem = root.FindName("SettingsNavItem");
            await Task.Delay(60);
            if (scale.ScaleX != 1 || scale.ScaleY != 1 || (animationsEnabled && scale.TranslateY <= 0))
                throw new InvalidOperationException("Sidebar navigation must use the default entrance without zoom.");
            await Task.Delay(400);
            AssertRestored(pages);
            var settings = Field<SettingsPage>(window, "_settingsPage");
            foreach (var category in new string?[] { "appearance", null })
            {
                typeof(SettingsPage).GetMethod("NavigateCategory", flags)!.Invoke(settings, [category]);
                await Task.Delay(60);
                var target = (ScrollViewer)settings.FindName(category is null ? "SettingsHome" : "AppearancePage");
                if (animationsEnabled && (target.RenderTransform is not TranslateTransform translate || translate.X <= 0))
                    throw new InvalidOperationException("Settings forward and back navigation must both slide from the right.");
                await Task.Delay(400);
                if (target.RenderTransform is TranslateTransform completed && Math.Abs(completed.X) > 0.001)
                    throw new InvalidOperationException("Settings slide must restore the page position.");
            }
            Program.Results.Add(new { theme = theme.ToString(), animationsEnabled, midScale, midOpacity,
                control = "new-tab DrillIn, default sidebar entrance, settings slide from right, completion and rapid switching", passed = true });
        }
        root.RequestedTheme = ElementTheme.Default;
        shell.SelectSession(first);
        if (Program.KeepAnimationPreview)
        {
            File.WriteAllText(Program.ReportPath, JsonSerializer.Serialize(new { passed = true, checks = Program.Results },
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        await first.DisposeAsync();
        await second.DisposeAsync();
        window.Close();
        Program.Finish();
    }

    private static void AssertRestored(FrameworkElement presenter)
    {
        var (scaleX, scaleY) = presenter.RenderTransform switch
        {
            ScaleTransform scale => (scale.ScaleX, scale.ScaleY),
            CompositeTransform composite => (composite.ScaleX, composite.ScaleY),
            _ => throw new InvalidOperationException("Unexpected entrance transform.")
        };
        if (Math.Abs(scaleX - 1) > 0.001 || Math.Abs(scaleY - 1) > 0.001 || Math.Abs(presenter.Opacity - 1) > 0.001 ||
            presenter.RenderTransform is CompositeTransform position && Math.Abs(position.TranslateY) > 0.001)
            throw new InvalidOperationException("Completed or interrupted animations must restore scale and opacity.");
    }
}
