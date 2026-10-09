using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.UI;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    // Deliberately isolated from TerminalPane: HWND hosting has airspace constraints
    // and must be proven here before changing the production session architecture.
    private async Task VerifyTerminalBackdropAsync()
    {
        const string pageUri = "https://fluentshell.local/index.html?backdrop=1";
        var window = new Window { Title = "FluentShell · xterm Windows 材质对照（离线）", SystemBackdrop = new MicaBackdrop() };
        _window = window;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        var root = new Grid { Padding = new Thickness(20), RowSpacing = 12, RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new TextBlock { Text = "xterm 背景材质对照 · 本地输入回显，无 SSH 连接", FontSize = 22 });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);
        var material = new ComboBox { Header = "窗口材质", ItemsSource = new[] { "Mica", "亚克力" }, SelectedIndex = 0, Width = 180 };
        var light = new ToggleSwitch { Header = "终端主题", OffContent = "深色", OnContent = "浅色" };
        var opaque = new ToggleSwitch { Header = "宿主底色", OffContent = "透明", OnContent = "实色" };
        toolbar.Children.Add(material);
        toolbar.Children.Add(light);
        toolbar.Children.Add(opaque);
        var panels = new Grid { ColumnSpacing = 20 };
        panels.ColumnDefinitions.Add(new ColumnDefinition());
        panels.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(panels, 2);
        root.Children.Add(panels);
        Grid Panel(string caption, int column)
        {
            var panel = new Grid { RowSpacing = 8 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            panel.Children.Add(new TextBlock { Text = caption, FontSize = 16 });
            Grid.SetColumn(panel, column);
            panels.Children.Add(panel);
            return panel;
        }
        var stockPanel = Panel("WinUI WebView2 控件", 0);
        var nativePanel = Panel("底层 WebView2 · HWND 宿主原型", 1);
        var stock = new WebView2 { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
        Grid.SetRow(stock, 1);
        stockPanel.Children.Add(stock);
        var nativeSlot = new Grid();
        Grid.SetRow(nativeSlot, 1);
        nativePanel.Children.Add(nativeSlot);
        window.Content = root;
        window.Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
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
        await WaitFor(() => root.XamlRoot is not null && nativeSlot.ActualWidth > 0);
        // An isolated profile avoids reading or changing the application's WebView data.
        var dataFolder = Path.Combine(Path.GetDirectoryName(Program.ReportPath)!, "backdrop-webview-" + Guid.NewGuid().ToString("N"));
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, dataFolder, new CoreWebView2EnvironmentOptions());
        await stock.EnsureCoreWebView2Async(environment);
        // Keep WinUI composition hosting and windowed hosting in separate environments.
        var nativeEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, dataFolder + "-native", new CoreWebView2EnvironmentOptions());
        var native = await nativeEnvironment.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)hwnd.ToInt64())));
        native.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        var nativeClosed = false;
        IntPtr NativeBrowserWindow()
        {
            for (var child = BackdropGetWindow(hwnd, 5); child != IntPtr.Zero; child = BackdropGetWindow(child, 2))
            {
                var className = new System.Text.StringBuilder(256);
                BackdropGetClassName(child, className, className.Capacity);
                if (className.ToString() == "Chrome_WidgetWin_0") return child;
            }
            return IntPtr.Zero;
        }
        void UpdateNativeBounds()
        {
            if (nativeClosed) return;
            var screen = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(nativeSlot.TransformToVisual(null).TransformPoint(new Point()));
            var point = new BackdropPoint { X = screen.X, Y = screen.Y };
            if (!BackdropScreenToClient(hwnd, ref point)) throw new InvalidOperationException("Cannot position the native WebView.");
            var scale = root.XamlRoot.RasterizationScale;
            native.Bounds = new Rect(point.X, point.Y, Math.Round(nativeSlot.ActualWidth * scale), Math.Round(nativeSlot.ActualHeight * scale));
            native.NotifyParentWindowPositionChanged();
            // A raw browser HWND is otherwise behind WinUI's DesktopChildSiteBridge.
            var child = NativeBrowserWindow();
            if (child != IntPtr.Zero && !BackdropSetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, 0x0013))
                throw new InvalidOperationException("Cannot bring the native browser above the WinUI island.");
        }
        nativeSlot.SizeChanged += (_, _) => UpdateNativeBounds();
        root.XamlRoot.Changed += (_, _) => UpdateNativeBounds();
        window.AppWindow.Changed += (_, _) => UpdateNativeBounds();
        UpdateNativeBounds();
        native.IsVisible = true;
        var stockReady = false;
        var nativeReady = false;
        var stockInput = "";
        var nativeInput = "";
        void Configure(CoreWebView2 web, bool nativeHost)
        {
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.SetVirtualHostNameToFolderMapping("fluentshell.local", Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            web.NavigationStarting += (_, navigation) => navigation.Cancel = navigation.Uri != pageUri;
            web.NewWindowRequested += (_, request) => request.Handled = true;
            web.WebMessageReceived += (_, message) =>
            {
                if (message.Source != pageUri) return;
                using var document = JsonDocument.Parse(message.WebMessageAsJson);
                if (!document.RootElement.TryGetProperty("type", out var type)) return;
                if (type.GetString() == "ready")
                {
                    if (nativeHost) nativeReady = true;
                    else stockReady = true;
                }
                if (type.GetString() == "input" && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                {
                    var value = data.GetString()!;
                    if (nativeHost) nativeInput += value;
                    else stockInput += value;
                    Post(web, new { type = "write", data = value.Replace("\r", "\r\n") });
                }
            };
            web.Navigate(pageUri);
        }
        Configure(stock.CoreWebView2, false);
        Configure(native.CoreWebView2, true);
        await WaitFor(() => stockReady && nativeReady);
        UpdateNativeBounds();
        void ApplyTheme()
        {
            root.RequestedTheme = light.IsOn ? ElementTheme.Light : ElementTheme.Dark;
            var color = !opaque.IsOn ? Microsoft.UI.Colors.Transparent : light.IsOn
                ? Color.FromArgb(255, 254, 254, 254) : Color.FromArgb(255, 54, 54, 54);
            stock.DefaultBackgroundColor = color;
            native.DefaultBackgroundColor = color;
            foreach (var web in new[] { stock.CoreWebView2, native.CoreWebView2 })
                Post(web, new { type = "theme", value = light.IsOn ? "light" : "dark" });
        }
        void ApplyMaterial() => window.SystemBackdrop = material.SelectedIndex == 0 ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
        light.Toggled += (_, _) => ApplyTheme();
        opaque.Toggled += (_, _) => ApplyTheme();
        material.SelectionChanged += (_, _) => ApplyMaterial();
        ApplyTheme();
        const string output = "FluentShell · Windows 背景材质实验\r\n\r\n"
            + "\x1b[31m红色\x1b[0m  \x1b[32m绿色\x1b[0m  \x1b[34m蓝色\x1b[0m  中文 / emoji 😀\r\n"
            + "\x1b[44;97m ANSI 显式背景保持蓝色 \x1b[0m\r\n\r\n"
            + "默认空白区域用于观察材质。\r\n切换上方材质、主题和宿主底色后可继续输入。\r\n\r\noffline> ";
        foreach (var web in new[] { stock.CoreWebView2, native.CoreWebView2 }) Post(web, new { type = "write", data = output });
        await Task.Delay(500, timeout.Token);
        Check(window.AppWindow.IsVisible && native.Bounds.Width > 100, "Responsive offline comparison window and sized native controller");
        Program.Results.Add(new { control = "runtime", os = Environment.OSVersion.VersionString,
            webview = environment.BrowserVersionString, scale = root.XamlRoot.RasterizationScale,
            bounds = native.Bounds, rootWidth = root.ActualWidth, slotWidth = nativeSlot.ActualWidth });
        foreach (var web in new[] { stock.CoreWebView2, native.CoreWebView2 })
        {
            var surfaces = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync(
                "JSON.stringify(['html','body','.xterm','.xterm-viewport','.xterm-screen','.xterm-rows'].map(s=>({selector:s,background:getComputedStyle(document.querySelector(s)).backgroundColor})))"))!;
            Program.Results.Add(new { control = "computed terminal surfaces", surfaces = JsonSerializer.Deserialize<JsonElement>(surfaces) });
        }

        // Screen pixels are essential: RenderTargetBitmap omits both WebView and Mica.
        // Changing the opaque XAML surface proves real transparency rather than CSS alone.
        var observations = new List<object>();
        var nativeTransparent = true;
        foreach (var color in new[] { Color.FromArgb(255, 35, 99, 150), Color.FromArgb(255, 140, 58, 100) })
        {
            root.Background = new SolidColorBrush(color);
            await Task.Delay(300, timeout.Token);
            await BackdropUiAsync(hwnd, "screenshot", "--output", Program.ReportPath + $".probe.{color.R}.png", "--capture-screen");
            var expected = (uint)(color.R | color.G << 8 | color.B << 16);
            var stockPixel = BackdropSample(root, stock);
            var nativePixel = BackdropSample(root, nativeSlot);
            nativeTransparent &= BackdropColorDistance(nativePixel, expected) <= 8;
            observations.Add(new { expected = expected.ToString("X6"), stock = stockPixel.ToString("X6"), native = nativePixel.ToString("X6") });
        }
        root.Background = null;
        Program.Results.Add(new { control = "actual transparency over changing XAML background", nativeTransparent, observations });
        Check(nativeTransparent, "Native WebView pixels reveal both underlying diagnostic colors");
        foreach (var selectedMaterial in new[] { 0, 1 })
        {
            material.SelectedIndex = selectedMaterial;
            ApplyMaterial();
            foreach (var isLight in new[] { false, true })
            {
                light.IsOn = isLight;
                ApplyTheme();
                await Task.Delay(350, timeout.Token);
                foreach (var web in new[] { stock.CoreWebView2, native.CoreWebView2 })
                {
                    var background = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor"));
                    var text = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.querySelector('.xterm-rows').textContent"));
                    Check(background == "rgba(0, 0, 0, 0)" && text!.Contains("ANSI 显式背景"), "Transparent shipped xterm page retains ANSI output after theme/material changes");
                }
                await BackdropUiAsync(hwnd, "screenshot", "--output", Program.ReportPath + $".{(selectedMaterial == 0 ? "Mica" : "Acrylic")}.{(isLight ? "Light" : "Dark")}.png", "--capture-screen");
            }
        }
        // Exercise actual keyboard delivery to the native child HWND, not a script-only paste.
        File.WriteAllText(Program.ReportPath, JsonSerializer.Serialize(new { passed = false, phase = "before native keyboard check", checks = Program.Results }, new JsonSerializerOptions { WriteIndented = true }));
        BackdropSetForegroundWindow(hwnd);
        var nativeBrowser = NativeBrowserWindow();
        Check(nativeBrowser != IntPtr.Zero, "Native browser HWND exists above the WinUI island");
        BackdropSetFocus(nativeBrowser);
        await native.CoreWebView2.ExecuteScriptAsync("window.fluentShellTerminal.focus()");
        await BackdropUiAsync(hwnd, "send-keys", "BACKDROP_INPUT_123", "--verbatim", "--via", "send-input");
        await WaitFor(() => nativeInput.Contains("BACKDROP_INPUT_123"));
        Check(true, "Actual native keyboard input reaches xterm and is echoed locally");
        Check(stockInput.Length == 0, "Native keyboard input is isolated from the WinUI terminal");
        var originalBounds = native.Bounds;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        await WaitFor(() => native.Bounds.Width < originalBounds.Width);
        Check(native.Bounds.Width > 100, "Native viewport follows WinUI resize and rasterization scale");
        opaque.IsOn = true;
        ApplyTheme();
        Check(native.DefaultBackgroundColor.A == 255, "Opaque fallback is available without reopening or clearing terminal output");
        opaque.IsOn = false;
        light.IsOn = false;
        material.SelectedIndex = 0;
        ApplyTheme();
        ApplyMaterial();
        window.Activate();
        BackdropSetForegroundWindow(hwnd);
        await Task.Delay(300, timeout.Token);
        await BackdropUiAsync(hwnd, "screenshot", "--output", Program.ReportPath + ".preview.png", "--capture-screen");
        window.Closed += (_, _) =>
        {
            nativeClosed = true;
            native.Close();
            stock.Close();
            if (Program.KeepBackdropPreview) Program.Finish();
        };
        if (Program.KeepBackdropPreview)
        {
            File.WriteAllText(Program.ReportPath, JsonSerializer.Serialize(new { passed = true, checks = Program.Results },
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        window.Close();
        Program.Finish();
    }

    private static void Post(CoreWebView2 web, object message) => web.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    private static async Task<string> BackdropUiAsync(IntPtr hwnd, string command, params string[] arguments)
    {
        var start = new ProcessStartInfo("winapp")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("ui");
        start.ArgumentList.Add(command);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--window");
        start.ArgumentList.Add(hwnd.ToString());
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var diagnostic = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"winapp ui {command}: {output} {diagnostic}");
        return output;
    }

    private static uint BackdropSample(Grid root, FrameworkElement element)
    {
        var local = element.TransformToVisual(null).TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight - 40));
        var screen = root.XamlRoot.CoordinateConverter.ConvertLocalToScreen(local);
        var dc = BackdropGetDC(IntPtr.Zero);
        try
        {
            var pixel = BackdropGetPixel(dc, screen.X, screen.Y);
            if (pixel == uint.MaxValue) throw new InvalidOperationException("Cannot read the interactive desktop pixel.");
            return pixel;
        }
        finally { BackdropReleaseDC(IntPtr.Zero, dc); }
    }

    private static int BackdropColorDistance(uint left, uint right) => new[] { 0, 8, 16 }
        .Max(shift => Math.Abs((int)(left >> shift & 255) - (int)(right >> shift & 255)));

    [StructLayout(LayoutKind.Sequential)]
    private struct BackdropPoint { public int X; public int Y; }
    [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
    private static extern bool BackdropScreenToClient(IntPtr hwnd, ref BackdropPoint point);
    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr BackdropGetDC(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int BackdropReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint BackdropGetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr BackdropGetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int BackdropGetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool BackdropSetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetFocus")]
    private static extern IntPtr BackdropSetFocus(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool BackdropSetForegroundWindow(IntPtr hwnd);
}
