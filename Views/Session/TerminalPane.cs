using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;
using FluentShell.Models;
using Windows.ApplicationModel.DataTransfer;

namespace FluentShell.Views.Session;

public sealed class TerminalResizeRequestedEventArgs : EventArgs
{
    public required int Columns { get; init; }
    public required int Rows { get; init; }
}

public sealed class TerminalPane : UserControl, IDisposable
{
    private readonly WebView2 _terminalView = new();
    private readonly StringBuilder _pendingOutput = new();
    private bool _initializationStarted;
    private bool _ready;
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
        if (_pasting) return;
        _pasting = true;
        try
        {
            if (text is null)
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                text = await content.GetTextAsync();
            }
            if (string.IsNullOrEmpty(text)) return;
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
        Content = _terminalView;
        _terminalView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _terminalView.VerticalAlignment = VerticalAlignment.Stretch;
        _terminalView.Loaded += TerminalView_Loaded;
        ActualThemeChanged += TerminalPane_ActualThemeChanged;
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
            var terminalAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            _terminalView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _terminalView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _terminalView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "fluentshell.local",
                terminalAssets,
                CoreWebView2HostResourceAccessKind.Allow);
            _terminalView.CoreWebView2.WebMessageReceived += TerminalView_WebMessageReceived;
            _terminalView.Source = new Uri("https://fluentshell.local/index.html");
        }
        catch (Exception ex)
        {
            InitializationFailed?.Invoke(this, ex.Message);
        }
    }

    private void TerminalView_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Source != "https://fluentshell.local/index.html") return;
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

    private void UpdateTheme()
    {
        var light = _preferences.TerminalTheme == "light" || (_preferences.TerminalTheme == "system" && ActualTheme == ElementTheme.Light);
        PostMessage(new { type = "theme", value = light ? "light" : "dark", colors = light ? _colors.Light : _colors.Dark });
    }

    private void PostMessage(object message)
    {
        if (!_ready || _terminalView.CoreWebView2 is null) return;
        _terminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void Dispose()
    {
        ActualThemeChanged -= TerminalPane_ActualThemeChanged;
        _terminalView.Loaded -= TerminalView_Loaded;
        if (_terminalView.CoreWebView2 is not null)
            _terminalView.CoreWebView2.WebMessageReceived -= TerminalView_WebMessageReceived;
    }
}
