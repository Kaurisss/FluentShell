using System.Reflection;
using System.Text.Json;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views;
using FluentShell.Views.Session;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.ThemeSmoke;

internal static class Program
{
    internal static readonly List<object> Results = [];
    internal static string ReportPath = Path.GetFullPath("theme-smoke.json");
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0) ReportPath = Path.GetFullPath(args[0]);
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
        try
        {
            _window = new Window { Title = "FluentShell offline theme regression" };
            var root = new Grid { RequestedTheme = ElementTheme.Light };
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var terminal = Field<TerminalPane>(workspace, "_terminalPane");
            var web = Field<WebView2>(terminal, "_terminalView");
            while (!Field<bool>(terminal, "_ready")) await Task.Delay(100, timeout.Token);
            terminal.Write("Offline output survives theme changes\r\n");
            var failures = new List<string>();
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
            await workspace.DisposeAsync();
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            Program.Finish();
        }
        catch (Exception e) { Program.Finish(e); }
    }
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
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
