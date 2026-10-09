using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Tests;
using FluentShell.Views;
using FluentShell.Views.Session;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifySessionBackdropAsync()
    {
        var window = new MainWindow { Title = "FluentShell · SSH 会话材质验证（离线）" };
        _window = window;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("_loaded", flags)!.SetValue(window, true);
        var root = (Grid)window.Content;
        root.RequestedTheme = ElementTheme.Dark;
        var presenter = (ContentPresenter)root.FindName("SessionContentPresenter");
        var host = Field<SessionHost>(window, "_sessionHost");
        var shell = Field<ShellCoordinator>(window, "_shell");
        var coordinator = Field<SessionCoordinator<IShellSession>>(shell, "_sessions");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // Keep all screen captures inside the interactive monitor, even when the
        // production window's preferred minimum is wider than the test desktop.
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenterSettings)
        {
            presenterSettings.PreferredMinimumWidth = 1200;
            presenterSettings.PreferredMinimumHeight = 750;
        }
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        window.AppWindow.Move(new Windows.Graphics.PointInt32(20, 20));
        var fixtureFolder = Path.Combine(Path.GetDirectoryName(Program.ReportPath)!, "session-backdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureFolder);
        var preferences = new UserPreferences();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        async Task WaitFor(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
        void Check(bool passed, string description)
        {
            Program.Results.Add(new { control = description, passed });
            if (!passed) throw new InvalidOperationException(description);
        }
        async Task Screen(string phase)
        {
            for (var attempt = 0; ; attempt++)
            {
                window.Activate();
                BackdropSetForegroundWindow(hwnd);
                await Task.Delay(150, timeout.Token);
                try
                {
                    await BackdropUiAsync(hwnd, "screenshot", "--output", Program.ReportPath + $".{phase}.png", "--capture-screen");
                    return;
                }
                catch (InvalidOperationException error) when (attempt < 2 && error.Message.Contains("foreground_not_target"))
                {
                    await Task.Delay(200, timeout.Token);
                }
            }
        }
        // The production page is inspected, rather than a second test implementation.
        async Task<string?> ReadScript(WebView2 web, string script) =>
            JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(script));
        async Task<string> WaitForOutput(WebView2 web, string marker)
        {
            // xterm batches writes and renders them on a subsequent animation frame.
            // A newly attached WebView may need longer than a fixed 200-ms delay.
            while (true)
            {
                var text = await ReadScript(web, "document.querySelector('.xterm-rows').textContent") ?? "";
                if (text.Contains(marker)) return text;
                await Task.Delay(50, timeout.Token);
            }
        }
        async Task Background(WebView2 web, string expected)
        {
            await Task.Delay(150, timeout.Token);
            Check(await ReadScript(web, "getComputedStyle(document.body).backgroundColor") == expected,
                "Production session background: " + expected);
        }
        async Task Wheel(WebView2 web, int deltaY)
        {
            // xterm 6's Chromium event normalizer reads the legacy wheelDeltaY
            // property first; synthetic WheelEvent instances otherwise expose zero.
            await web.CoreWebView2.ExecuteScriptAsync($$"""
                (() => {
                    const wheel = new WheelEvent('wheel', { view: window, bubbles: true, cancelable: true, deltaY: {{deltaY}} });
                    Object.defineProperty(wheel, 'wheelDeltaY', { value: {{-deltaY}} });
                    document.querySelector('.xterm-scrollable-element').dispatchEvent(wheel);
                })()
                """);
        }
        SessionWorkspace Create(BackdropConnection connection)
        {
            var workspace = new SessionWorkspace(new ServerProfile { Name = "离线 SSH 会话", Host = "offline.invalid", Username = "fixture" }, hwnd,
                (_, _) => Task.FromResult<ISshConnection?>(connection), _ => Task.FromResult(false), () => Task.FromResult<string?>("fixture"));
            workspace.SetPreferences(preferences, fixtureFolder);
            typeof(ShellCoordinator).GetMethod("SubscribeSession", flags)!.Invoke(shell, [workspace]);
            coordinator.Add(workspace);
            host.Add(workspace);
            return workspace;
        }
        window.Activate();
        var firstConnection = new BackdropConnection();
        var secondConnection = new BackdropConnection();
        var first = Create(firstConnection);
        var second = Create(secondConnection);
        var firstTerminal = Field<TerminalPane>(first, "_terminalPane");
        var secondTerminal = Field<TerminalPane>(second, "_terminalPane");
        var firstWeb = Field<WebView2>(firstTerminal, "_terminalView");
        var secondWeb = Field<WebView2>(secondTerminal, "_terminalView");
        shell.SelectSession(first);
        await WaitFor(() => Field<bool>(firstTerminal, "_ready"));
        await first.ConnectAsync();
        const string output = "FIRST_SSH_SESSION · 中文 😀\r\n\x1b[44;97mANSI 蓝色背景\x1b[0m\r\n<script>window.remoteExecuted=true</script>\r\nssh> ";
        await Task.Run(() => firstConnection.Emit(output));
        await Task.Delay(200, timeout.Token);
        Check(first.IsConnected, "Production SessionConnection connects through the offline transport");
        await Background(firstWeb, "rgba(0, 0, 0, 0)");
        var contentLayer = Descendants((NavigationView)root.FindName("RootNavigationView")).OfType<Grid>()
            .First(grid => grid.Name == "ContentGrid" && grid.Background is SolidColorBrush);
        var terminalFrame = (Border)VisualTreeHelper.GetParent(firstTerminal);
        var terminalOrigin = firstTerminal.TransformToVisual(terminalFrame).TransformPoint(new Windows.Foundation.Point());
        var scale = root.XamlRoot.RasterizationScale;
        Program.Results.Add(new { control = "terminal border layout", scale,
            thickness = terminalFrame.BorderThickness.Left, origin = new { terminalOrigin.X, terminalOrigin.Y },
            frame = new { terminalFrame.ActualWidth, terminalFrame.ActualHeight },
            terminal = new { firstTerminal.ActualWidth, firstTerminal.ActualHeight } });
        Check(Math.Abs(terminalFrame.BorderThickness.Left * scale - 2) < 0.001 &&
            Math.Abs(terminalOrigin.X * scale - 2) < 0.1 && Math.Abs(terminalOrigin.Y * scale - 2) < 0.1 &&
            Math.Abs((terminalFrame.ActualWidth - firstTerminal.ActualWidth) * scale - 4) < 0.1 &&
            Math.Abs((terminalFrame.ActualHeight - firstTerminal.ActualHeight) * scale - 4) < 0.1,
            $"Terminal frame occupies exactly 2 physical pixels on all four sides at {scale:P0} scaling");
        var sftp = Field<SftpWorkspaceView>(first, "_sftpView");
        var pathFields = new[] { (TextBox)sftp.FindName("LocalPathBox"), (TextBox)sftp.FindName("PathBox") };
        var tables = new[] { (Control)sftp.FindName("LocalFiles"), (Control)sftp.FindName("RemoteTable") };
        var tableFrames = new[] { (Border)sftp.FindName("LocalFilesFrame"), (Border)sftp.FindName("RemoteTableFrame") };
        Check(terminalFrame.CornerRadius == pathFields[0].CornerRadius && terminalFrame.CornerRadius.TopLeft > 0 &&
            tableFrames.All(frame => frame.CornerRadius == terminalFrame.CornerRadius) &&
            tables.All(table => table.BorderThickness == new Thickness(0)),
            "Terminal and file grids share the path inputs' system corner radius with a single outer stroke");
        Check(await firstWeb.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('.xterm-scrollable-element > .scrollbar')].every(e => getComputedStyle(e).display === 'none')") == "true",
            "Production xterm scrollbar chrome is hidden");
        Check(((SolidColorBrush)firstWeb.Resources["BrushForThemeBackgroundColor"]).Color == ((SolidColorBrush)contentLayer.Background).Color &&
            !firstWeb.Resources.ContainsKey("BrushForThemeBackgroundColor_HC"),
            "Composition bridge shares the navigation content tint and leaves the SDK high-contrast resource untouched");
        var materialPixel = BackdropSample(root, firstWeb);

        // Actual keyboard input must traverse xterm -> TerminalPane -> SessionConnection.
        if (!Program.SessionAppearanceOnly)
        {
        firstWeb.Focus(FocusState.Programmatic);
        firstTerminal.FocusTerminal();
        if (GetForegroundWindow() != hwnd)
        {
            var paneButton = (Button)root.FindName("PaneToggleButton");
            var focusResult = await BackdropUiAsync(hwnd, "focus", Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(paneButton));
            Program.Results.Add(new { control = "offline foreground activation", focusResult });
        }
        BackdropSetForegroundWindow(hwnd);
        await Task.Delay(150, timeout.Token);
        // winapp's target identity check rejects WebView's cross-process UIA provider.
        // Send native input only after verifying this test window owns the desktop point.
        var point = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
            firstWeb.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(firstWeb.ActualWidth / 2, firstWeb.ActualHeight / 2)));
        var mousePoint = new MouseMessagePoint { X = point.X, Y = point.Y };
        Check(GetForegroundWindow() == hwnd && GetAncestor(WindowFromPoint(mousePoint), 2) == hwnd,
            $"Native input is confined to the foreground offline window: foreground={GetForegroundWindow()}, expected={hwnd}, pointerRoot={GetAncestor(WindowFromPoint(mousePoint), 2)}, point={point.X},{point.Y}");
        Check(GetCursorPos(out var cursor) && SetCursorPos(point.X, point.Y), "Native pointer can focus the terminal");
        try
        {
            var click = new[] { new NativeInput { Mouse = new() { Flags = 2 } }, new NativeInput { Mouse = new() { Flags = 4 } } };
            Check(SendInput(2, click, Marshal.SizeOf<NativeInput>()) == 2, "Real pointer click reaches the terminal surface");
            await Task.Delay(100, timeout.Token);
            Check(await ReadScript(firstWeb, "document.activeElement.tagName") == "TEXTAREA", "xterm input receives focus after the native click");
            var keys = "SSH_BACKDROP_INPUT".SelectMany(character => new[]
            {
                new BackdropKeyInput { Type = 1, Data = new() { Keyboard = new() { ScanCode = character, Flags = 4 } } },
                new BackdropKeyInput { Type = 1, Data = new() { Keyboard = new() { ScanCode = character, Flags = 6 } } }
            }).ToArray();
            Check(GetForegroundWindow() == hwnd && BackdropSendKeyInput((uint)keys.Length, keys, Marshal.SizeOf<BackdropKeyInput>()) == keys.Length,
                "Real keyboard characters are delivered to the offline terminal");
        }
        finally { if (GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y) SetCursorPos(cursor.X, cursor.Y); }
        await WaitFor(() => firstConnection.Input.Contains("SSH_BACKDROP_INPUT"));
        Check(secondConnection.Input.Length == 0, "Keyboard input reaches only the active SSH transport");
        await Screen("Mica.Dark");
        }
        var oldResizeCount = firstConnection.Sizes.Count;
        first.SetTerminalFontSize(18);
        await WaitFor(() => firstConnection.Sizes.Count > oldResizeCount);
        Check(firstConnection.Sizes.All(size => size.Columns > 0 && size.Rows > 0), "xterm resize reaches the SSH PTY with positive dimensions");
        first.SetTerminalFontSize(14);

        first.SetPreferences(preferences with { TerminalBackdrop = false }, fixtureFolder);
        await Background(firstWeb, "rgb(54, 54, 54)");
        await Screen("opaque");
        var opaquePixel = BackdropSample(root, firstWeb);
        Program.Results.Add(new { control = "actual session screen pixels", material = materialPixel.ToString("X6"), opaque = opaquePixel.ToString("X6") });
        Check(BackdropColorDistance(opaquePixel, 0x363636) <= 8 && BackdropColorDistance(materialPixel, opaquePixel) > 8,
            "Actual terminal pixels switch between system material and the saved opaque palette");
        first.SetPreferences(preferences, fixtureFolder);
        first.SetTerminalColors(new() { Dark = new() { ["background"] = "#112233", ["red"] = "#ABCDEF" } });
        await Background(firstWeb, "rgb(17, 34, 51)");
        first.SetTerminalColors(new());
        first.SetPreferences(preferences with { TerminalTheme = "light" }, fixtureFolder);
        await Background(firstWeb, "rgb(254, 254, 254)");
        first.SetPreferences(preferences, fixtureFolder);

        foreach (var acrylic in new[] { false, true })
        {
            window.SystemBackdrop = acrylic ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Background(firstWeb, "rgba(0, 0, 0, 0)");
                var stroke = ((SolidColorBrush)terminalFrame.BorderBrush).Color;
                Check(tables.All(table => table.BorderBrush is SolidColorBrush brush && brush.Color == stroke) &&
                    tableFrames.All(frame => frame.BorderBrush is SolidColorBrush brush && brush.Color == stroke) &&
                    pathFields.All(path => path.BorderBrush is SolidColorBrush brush && brush.Color == stroke),
                    "Terminal, both path inputs and both file grids share the current theme's border color");
                foreach (var path in pathFields)
                {
                    var border = Descendants(path).OfType<Border>().Single(element => element.Name == "BorderElement");
                    Check(VisualStateManager.GoToState(path, "PointerOver", false), "Path input supports the native hover state");
                    await Task.Delay(30, timeout.Token);
                    Check(border.BorderBrush is SolidColorBrush hover && hover.Color == stroke,
                        "Path input's rendered hover border matches the file grids");
                    Check(path.Focus(FocusState.Programmatic), "Path input accepts keyboard focus");
                    await Task.Delay(50, timeout.Token);
                    Check(border.BorderBrush is LinearGradientBrush, "Focused path input retains the native accent underline");
                    firstWeb.Focus(FocusState.Programmatic);
                    firstTerminal.FocusTerminal();
                    await Task.Delay(50, timeout.Token);
                    Check(VisualStateManager.GoToState(path, "Normal", false) &&
                        border.BorderBrush is SolidColorBrush normal && normal.Color == stroke,
                        "Path input's rendered normal border matches the file grids");
                }
                var text = await ReadScript(firstWeb, "document.querySelector('.xterm-rows').textContent");
                Check(text!.Contains("FIRST_SSH_SESSION") && text.Contains("<script>") &&
                    await firstWeb.CoreWebView2.ExecuteScriptAsync("window.remoteExecuted === true") == "false",
                    "SSH output and ANSI cells survive material/theme changes; remote text stays inert");
                Check(await ReadScript(firstWeb, "getComputedStyle([...document.querySelectorAll('.xterm-rows span')].find(e=>e.textContent.includes('ANSI'))).backgroundColor") != "rgba(0, 0, 0, 0)",
                    "Explicit remote ANSI cell backgrounds remain opaque");
                var phase = $"{(acrylic ? "Acrylic" : "Mica")}.{theme}";
                await Screen(phase);
                using var imageFile = File.OpenRead(Program.ReportPath + $".{phase}.png");
                using var imageStream = imageFile.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(imageStream);
                var imageData = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                Check(BackdropGetWindowRect(hwnd, out var windowBounds) &&
                    decoder.PixelWidth == windowBounds.Right - windowBounds.Left &&
                    decoder.PixelHeight == windowBounds.Bottom - windowBounds.Top,
                    "Screen capture matches the verification window's physical bounds");
                uint Pixel(int x, int y)
                {
                    var offset = checked(((y - windowBounds.Top) * (int)decoder.PixelWidth + x - windowBounds.Left) * 4);
                    return (uint)(imageData[offset + 2] | imageData[offset + 1] << 8 | imageData[offset] << 16);
                }
                var topLeft = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
                    terminalFrame.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()));
                var bottomRight = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
                    terminalFrame.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(terminalFrame.ActualWidth, terminalFrame.ActualHeight)));
                var midX = (topLeft.X + bottomRight.X) / 2;
                var midY = (topLeft.Y + bottomRight.Y) / 2;
                var edges = new[]
                {
                    Enumerable.Range(0, 3).Select(offset => Pixel(topLeft.X + offset, midY)).ToArray(),
                    Enumerable.Range(0, 3).Select(offset => Pixel(bottomRight.X - 1 - offset, midY)).ToArray(),
                    Enumerable.Range(0, 3).Select(offset => Pixel(midX, topLeft.Y + offset)).ToArray(),
                    Enumerable.Range(0, 3).Select(offset => Pixel(midX, bottomRight.Y - 1 - offset)).ToArray()
                };
                Program.Results.Add(new { control = "physical terminal border pixels", scale, theme = theme.ToString(), acrylic,
                    edges = edges.Select(edge => edge.Select(pixel => pixel.ToString("X6")).ToArray()).ToArray() });
                Check(edges.All(edge => BackdropColorDistance(edge[0], edge[1]) <= 1 && BackdropColorDistance(edge[1], edge[2]) > 8),
                    "Screen pixels show exactly two stroke pixels followed by the terminal surface on every edge");
                Check(BackdropColorDistance(Pixel(topLeft.X, topLeft.Y), Pixel(topLeft.X - 2, topLeft.Y)) <= 8 &&
                    BackdropColorDistance(Pixel(topLeft.X, topLeft.Y), edges[0][0]) > 8,
                    "Terminal outer corner is rounded in the actual screen capture");
                foreach (var frame in tableFrames)
                {
                    var corner = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
                        frame.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()));
                    var side = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
                        frame.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, frame.ActualHeight / 2)));
                    Check(BackdropColorDistance(Pixel(corner.X, corner.Y), Pixel(corner.X - 2, corner.Y)) <= 8 &&
                        BackdropColorDistance(Pixel(corner.X, corner.Y), Pixel(side.X, side.Y)) > 8,
                        "File-grid outer corner is rounded in the actual screen capture");
                }
                var terminalPoint = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
                    firstWeb.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(firstWeb.ActualWidth / 2, firstWeb.ActualHeight - 40)));
                var terminalPixel = Pixel(terminalPoint.X, terminalPoint.Y);
                var gap = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(
                    terminalFrame.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(terminalFrame.ActualWidth / 2, terminalFrame.ActualHeight + 2)));
                var contentPixel = Pixel(gap.X, gap.Y);
                var tint = ((SolidColorBrush)firstWeb.Resources["BrushForThemeBackgroundColor"]).Color;
                Program.Results.Add(new { control = "terminal and content surface pixels", theme = theme.ToString(), acrylic,
                    terminal = terminalPixel.ToString("X6"), content = contentPixel.ToString("X6"), tint = tint.ToString() });
                Check(tint == ((SolidColorBrush)contentLayer.Background).Color && BackdropColorDistance(terminalPixel, contentPixel) <= 8,
                    "Terminal matches the adjacent content surface across theme and material changes");
            }
        }
        if (Program.SessionAppearanceOnly)
        {
            firstConnection.Emit(string.Concat(Enumerable.Range(0, 180).Select(line => $"HISTORY_{line:D3}\r\n")));
            var bottomText = await WaitForOutput(firstWeb, "HISTORY_179");
            await Wheel(firstWeb, -240);
            await Task.Delay(200, timeout.Token);
            var scrolledText = await ReadScript(firstWeb, "document.querySelector('.xterm-rows').textContent");
            Check(scrolledText != bottomText && scrolledText!.Contains("HISTORY_"), "Mouse-wheel events still scroll terminal history with the scrollbar hidden");
            Check(await firstWeb.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('.xterm-scrollable-element > .scrollbar')].every(e => getComputedStyle(e).display === 'none')") == "true",
                "Scrollbar stays hidden when the terminal has scrollback");
            await Wheel(firstWeb, 100000);
            await WaitForOutput(firstWeb, "HISTORY_179");
        }
        window.SystemBackdrop = new MicaBackdrop();
        shell.SelectSession(second);
        await WaitFor(() => Field<bool>(secondTerminal, "_ready"));
        await second.ConnectAsync();
        secondConnection.Emit("SECOND_SSH_SESSION\r\n");
        firstConnection.Emit("BACKGROUND_SESSION_OUTPUT\r\n");
        var secondText = await WaitForOutput(secondWeb, "SECOND_SSH_SESSION");
        Check(secondText.Contains("SECOND_SSH_SESSION"), "Second SSH session displays its own transport output");
        shell.SelectSession(first);
        var firstText = await WaitForOutput(firstWeb, "BACKGROUND_SESSION_OUTPUT");
        Check(firstText.Contains("BACKGROUND_SESSION_OUTPUT") && !firstText.Contains("SECOND_SSH_SESSION"), "Detached SSH session keeps independent background output");

        host.Select(null);
        typeof(MainWindow).GetMethod("ShowUnconnectedLayout", flags)!.Invoke(window, ["settings", false]);
        await Task.Delay(250, timeout.Token);
        Check(presenter.Visibility == Visibility.Collapsed && presenter.Content is null, "Navigating to settings detaches the terminal");
        Check(Field<XamlRoot?>(first, "_terminalXamlRoot") is null, "Detached terminal releases the display-scale subscription");
        var settings = Field<SettingsPage>(window, "_settingsPage");
        typeof(SettingsPage).GetMethod("NavigateCategory", flags)!.Invoke(settings, ["terminal"]);
        await Task.Delay(300, timeout.Token);
        var setting = Descendants(settings).OfType<ToggleSwitch>().Single(toggle =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(toggle) == "终端使用窗口背景材质");
        Check(setting.IsOn && setting.IsTabStop, "Terminal backdrop setting is enabled by default and keyboard accessible");
        var help = (Button)settings.FindName("PageHelpButton");
        help.Flyout.ShowAt(help);
        await Task.Delay(150, timeout.Token);
        Check(((TextBlock)settings.FindName("PageHelpText")).Text.Contains("自定义背景色"), "Backdrop fallback explanation is available in the settings hint flyout");
        if (!Program.SessionAppearanceOnly) await Screen("settings");
        await HideFlyoutAsync(help.Flyout);
        root.RequestedTheme = ElementTheme.Light;
        shell.SelectSession(first);
        await Task.Delay(350, timeout.Token);
        Check(firstTerminal.ActualTheme == ElementTheme.Light && await ReadScript(firstWeb, "document.documentElement.style.colorScheme") == "light",
            "Cached terminal inherits the new theme when returning from settings");
        Check(Field<XamlRoot?>(first, "_terminalXamlRoot") == root.XamlRoot &&
            Math.Abs(terminalFrame.BorderThickness.Left * root.XamlRoot.RasterizationScale - 2) < 0.001,
            "Reattached terminal restores its display-scale subscription and two-physical-pixel stroke");
        await Background(firstWeb, "rgba(0, 0, 0, 0)");
        first.ExecuteShortcut("files");
        await Task.Delay(150, timeout.Token);
        Check(Field<bool>(first, "_isSftpCollapsed"), "SFTP collapse remains available with the composition terminal");
        first.ExecuteShortcut("files");
        await Task.Delay(150, timeout.Token);
        Check(!Field<bool>(first, "_isSftpCollapsed") && Field<SftpWorkspaceView>(first, "_sftpView").ActualHeight > 100,
            "SFTP restores below the terminal without overlapping it");

        var overlay = (FrameworkElement)root.FindName("ConnectionDialogOverlay");
        var beforeOverlay = BackdropSample(root, firstWeb);
        overlay.Visibility = Visibility.Visible;
        await Task.Delay(150, timeout.Token);
        if (!Program.SessionAppearanceOnly) await Screen("connection-overlay");
        Check(BackdropColorDistance(BackdropSample(root, firstWeb), beforeOverlay) > 8, "Connection overlay participates in XAML z-order above the terminal");
        overlay.Visibility = Visibility.Collapsed;
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "会话材质回归", Content = "终端会话保持连接", CloseButtonText = "关闭" };
        var showing = dialog.ShowAsync();
        await Task.Delay(200, timeout.Token);
        Check(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(), "WinUI dialog remains above the terminal without hiding a native child window");
        if (!Program.SessionAppearanceOnly) await Screen("dialog");
        dialog.Hide();
        await showing;

        var canceled = false;
        void Navigation(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs args) => canceled = args.Cancel;
        firstWeb.CoreWebView2.NavigationStarting += Navigation;
        firstWeb.CoreWebView2.Navigate("https://offline.invalid/untrusted");
        await WaitFor(() => canceled);
        firstWeb.CoreWebView2.NavigationStarting -= Navigation;
        Check(firstWeb.CoreWebView2.Source == "https://fluentshell.local/index.html?backdrop=1", "Navigation cannot move the privileged SSH bridge away from the local terminal page");

        await second.DisposeAsync();
        await first.DisposeAsync();
        foreach (var session in new[] { first, second })
            typeof(ShellCoordinator).GetMethod("UnsubscribeSession", flags)!.Invoke(shell, [session]);
        Check(firstConnection.Disposed && secondConnection.Disposed && !Field<bool>(firstTerminal, "_ready"), "Closing sessions releases transports and WebView lifetimes");
        Check(Field<XamlRoot?>(first, "_terminalXamlRoot") is null, "Closing the terminal releases the display-scale subscription");
        window.Close();
        Program.Finish();
    }

    private sealed class BackdropConnection : ISshConnection
    {
        public bool IsConnected { get; private set; }
        public bool Disposed { get; private set; }
        public string Input { get; private set; } = "";
        public List<(int Columns, int Rows)> Sizes { get; } = [];
        private readonly FakeSftpClient _browse = new();
        private readonly FakeSftpClient _transfers = new();
        public ISftpClient? SftpClient => IsConnected ? _browse : null;
        public ISftpClient? TransferSftpClient => IsConnected ? _transfers : null;
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired { add { } remove { } }
        public event EventHandler? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task SendRawAsync(string input)
        {
            Input += input;
            Emit(input);
            return Task.CompletedTask;
        }
        public void Emit(string output) => OutputReceived?.Invoke(this, output);
        public Task ResizeTerminalAsync(int columns, int rows)
        {
            Sizes.Add((columns, rows));
            return Task.CompletedTask;
        }
        public Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult<ServerMetrics?>(null);
        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BackdropKeyboard
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct BackdropKeyUnion { [FieldOffset(0)] public BackdropKeyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BackdropKeyInput { public uint Type; public BackdropKeyUnion Data; }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint BackdropSendKeyInput(uint count, BackdropKeyInput[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct BackdropWindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BackdropGetWindowRect(IntPtr hwnd, out BackdropWindowRect rectangle);
}
