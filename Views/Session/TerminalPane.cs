using Microsoft.UI.Xaml;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;
using FluentShell.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace FluentShell.Views.Session;

public sealed class TerminalResizeRequestedEventArgs : EventArgs
{
    public required int Columns { get; init; }
    public required int Rows { get; init; }
}

public sealed class TerminalPane : UserControl, IDisposable
{
    private const string TerminalUri = "https://fluentshell.local/index.html?backdrop=1";
    private readonly WebView2 _terminalView = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    // Keep the existing opaque terminal on platforms without Windows 11 materials.
    private readonly bool _backdropSupported = MicaController.IsSupported();
    private readonly StringBuilder _pendingOutput = new();
    private bool _initializationStarted;
    private bool _ready;
    private bool _disposed;
    private double _fontSize = 14;
    private TerminalColors _colors = new();
    private UserPreferences _preferences = new();
    private bool _pasting;
    public event EventHandler<string>? ShortcutRequested;

    public void SetPreferences(UserPreferences preferences)
    {
        _preferences = preferences.Normalize();
        PostPreferences();
        UpdateTheme();
    }

    public void Search() => PostMessage(new { type = "search" });

    private void PostPreferences() => PostMessage(new { type = "preferences", value = new {
        fontFamily = _preferences.FontFamily, cursorStyle = _preferences.CursorStyle,
        cursorBlink = _preferences.CursorBlink, scrollback = _preferences.Scrollback,
        copyOnSelect = _preferences.CopyOnSelect, rightClickPaste = _preferences.RightClickPaste,
        shortcuts = _preferences.Shortcuts()
    } });

    private async Task PasteAsync(string? text = null)
    {
        if (_disposed || _pasting) return;
        _pasting = true;
        try
        {
            if (text is null)
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                text = await content.GetTextAsync();
            }
            if (_disposed || string.IsNullOrEmpty(text)) return;
            if (_preferences.ConfirmMultilinePaste && (text.Contains('\n') || text.Contains('\r')))
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "确认多行粘贴",
                    Content = "粘贴内容包含换行，可能立即执行多条远程命令。是否继续？",
                    PrimaryButtonText = "粘贴", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            PostMessage(new { type = "paste", data = text });
        }
        catch (Exception) { /* Clipboard may be locked; never send partially read content. */ }
        finally { _pasting = false; }
    }

    public void SetColors(TerminalColors colors)
    {
        _colors = colors.Normalize();
        UpdateTheme();
    }

    public TerminalPane()
    {
        // Windows App SDK 2.3's WebView2 paints an opaque ContentExternalOutputLink
        // independently of DefaultBackgroundColor. Its local helper brush supplies
        // that bridge color (microsoft-ui-xaml WebView2::GetThemeBackgroundColor).
        // The external bridge bypasses NavigationView's content tint. Use the same
        // theme color as that XAML layer so the terminal and file tables blend alike.
        // Keep this workaround scoped to this control; retain the SDK's HC brush.
        _terminalView.Resources["BrushForThemeBackgroundColor"] = (SolidColorBrush)XamlReader.Load(
            "<SolidColorBrush xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Color='{ThemeResource LayerFillColorDefault}'/>");
        _terminalView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        Content = _terminalView;
        _terminalView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _terminalView.VerticalAlignment = VerticalAlignment.Stretch;
        _terminalView.Loaded += TerminalView_Loaded;
        ActualThemeChanged += TerminalPane_ActualThemeChanged;
        // AccessibilitySettings.HighContrastChanged requires a CoreWindow; desktop
        // WinUI uses system color notifications to refresh the fallback instead.
        _uiSettings.ColorValuesChanged += UISettings_ColorValuesChanged;
    }

    public event EventHandler<string>? InputReceived;
    public event EventHandler<TerminalResizeRequestedEventArgs>? ResizeRequested;
    public event EventHandler<string>? InitializationFailed;

    public void SetFontSize(double value)
    {
        _fontSize = value;
        PostMessage(new { type = "fontSize", value });
    }

    public void Write(string text)
    {
        if (_disposed) return;
        if (!_ready)
        {
            _pendingOutput.Append(text);
            if (_pendingOutput.Length > 1_000_000)
                _pendingOutput.Remove(0, _pendingOutput.Length - 800_000);
            return;
        }

        PostMessage(new { type = "write", data = text });
    }

    public void FocusTerminal() => PostMessage(new { type = "focus" });

    private void TerminalView_Loaded(object sender, RoutedEventArgs e)
    {
        // A cached WebView can miss theme updates while detached for settings navigation.
        // Re-send the inherited theme each time it returns to the live visual tree.
        UpdateTheme();
        if (_initializationStarted) return;
        _initializationStarted = true;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _terminalView.EnsureCoreWebView2Async();
            if (_disposed) return;
            var terminalAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            _terminalView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _terminalView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _terminalView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "fluentshell.local",
                terminalAssets,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _terminalView.CoreWebView2.WebMessageReceived += TerminalView_WebMessageReceived;
            _terminalView.CoreWebView2.NavigationStarting += TerminalView_NavigationStarting;
            _terminalView.CoreWebView2.NewWindowRequested += TerminalView_NewWindowRequested;
            _terminalView.Source = new Uri(TerminalUri);
        }
        catch (Exception ex)
        {
            if (!_disposed) InitializationFailed?.Invoke(this, ex.Message);
        }
    }

    private void TerminalView_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (_disposed || e.Source != TerminalUri) return;
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            switch (typeElement.GetString())
            {
                case "paste":
                    _ = PasteAsync(root.TryGetProperty("data", out var paste) && paste.ValueKind == JsonValueKind.String ? paste.GetString() : null);
                    break;
                case "copy" when _preferences.CopyOnSelect:
                    if (root.TryGetProperty("data", out var copy) && copy.ValueKind == JsonValueKind.String)
                    {
                        var package = new DataPackage();
                        package.SetText(copy.GetString() ?? "");
                        Clipboard.SetContent(package);
                    }
                    break;
                case "shortcut":
                    if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String &&
                        _preferences.Shortcuts().ContainsKey(action.GetString()!))
                        ShortcutRequested?.Invoke(this, action.GetString()!);
                    break;
                case "ready":
                    MarkReady();
                    break;
                case "input" when root.TryGetProperty("data", out var input):
                    if (input.GetString() is { Length: > 0 } data)
                        InputReceived?.Invoke(this, data);
                    break;
                case "resize" when root.TryGetProperty("cols", out var columns) &&
                    root.TryGetProperty("rows", out var rows):
                    ResizeRequested?.Invoke(this, new TerminalResizeRequestedEventArgs
                    {
                        Columns = columns.GetInt32(),
                        Rows = rows.GetInt32()
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            InitializationFailed?.Invoke(this, $"终端消息失败：{ex.Message}");
        }
    }

    private void MarkReady()
    {
        _ready = true;
        PostPreferences();
        UpdateTheme();
        PostMessage(new { type = "fontSize", value = _fontSize });
        if (_pendingOutput.Length == 0) return;

        var pending = _pendingOutput.ToString();
        _pendingOutput.Clear();
        PostMessage(new { type = "write", data = pending });
    }

    private void TerminalPane_ActualThemeChanged(FrameworkElement sender, object args) => UpdateTheme();

    private void UISettings_ColorValuesChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!_disposed) UpdateTheme(); });

    private void TerminalView_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) =>
        args.Cancel = args.Uri != TerminalUri;

    private void TerminalView_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private void UpdateTheme()
    {
        var light = _preferences.TerminalTheme == "light" || (_preferences.TerminalTheme == "system" && ActualTheme == ElementTheme.Light);
        var colors = light ? _colors.Light : _colors.Dark;
        var highContrast = _accessibility.HighContrast;
        var backdrop = _backdropSupported && _preferences.TerminalBackdrop && !highContrast &&
            light == (ActualTheme == ElementTheme.Light) && !colors.ContainsKey("background");
        if (highContrast)
        {
            var background = _uiSettings.GetColorValue(UIColorType.Background);
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            static string Hex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            colors = new(colors) { ["background"] = Hex(background), ["foreground"] = Hex(foreground),
                ["cursor"] = Hex(foreground), ["cursorAccent"] = Hex(background) };
        }
        PostMessage(new { type = "theme", value = light ? "light" : "dark", colors, backdrop });
    }

    private void PostMessage(object message)
    {
        if (_disposed || !_ready || _terminalView.CoreWebView2 is null) return;
        _terminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        _pendingOutput.Clear();
        ActualThemeChanged -= TerminalPane_ActualThemeChanged;
        _uiSettings.ColorValuesChanged -= UISettings_ColorValuesChanged;
        _terminalView.Loaded -= TerminalView_Loaded;
        if (_terminalView.CoreWebView2 is not null)
        {
            _terminalView.CoreWebView2.WebMessageReceived -= TerminalView_WebMessageReceived;
            _terminalView.CoreWebView2.NavigationStarting -= TerminalView_NavigationStarting;
            _terminalView.CoreWebView2.NewWindowRequested -= TerminalView_NewWindowRequested;
        }
        _terminalView.Close();
    }
}
