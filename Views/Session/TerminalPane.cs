using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;

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

    public TerminalPane()
    {
        Content = _terminalView;
        _terminalView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _terminalView.VerticalAlignment = VerticalAlignment.Stretch;
        _terminalView.Loaded += TerminalView_Loaded;
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
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            switch (typeElement.GetString())
            {
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
        PostMessage(new { type = "fontSize", value = _fontSize });
        if (_pendingOutput.Length == 0) return;

        var pending = _pendingOutput.ToString();
        _pendingOutput.Clear();
        PostMessage(new { type = "write", data = pending });
    }

    private void PostMessage(object message)
    {
        if (!_ready || _terminalView.CoreWebView2 is null) return;
        _terminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void Dispose()
    {
        _terminalView.Loaded -= TerminalView_Loaded;
        if (_terminalView.CoreWebView2 is not null)
            _terminalView.CoreWebView2.WebMessageReceived -= TerminalView_WebMessageReceived;
    }
}