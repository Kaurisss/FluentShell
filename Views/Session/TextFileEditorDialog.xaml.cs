using FluentShell.Models;
using FluentShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentShell.Views.Session;

public sealed partial class TextFileEditorDialog : ContentDialog
{
    private readonly ITextFileService _service;
    private readonly string _path;
    private readonly string _name;
    private readonly bool _remote;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private TextFileDocument? _document;
    private bool _saving;
    private bool _closed;
    private bool _discard;
    public bool WasSaved { get; private set; }

    public TextFileEditorDialog(ITextFileService service, string path, string name, bool remote, XamlRoot root, ElementTheme theme)
    {
        _service = service;
        _path = path;
        _name = name;
        _remote = remote;
        _token = _lifetime.Token;
        InitializeComponent();
        XamlRoot = root;
        RequestedTheme = theme;
        Title = name;
        PathText.Text = $"{(remote ? "远程文件" : "本地文件")} · {path}";
        ToolTipService.SetToolTip(PathText, path);
        UpdateSize();
        root.Changed += Root_Changed;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            root.Changed -= Root_Changed;
        };
        UpdateState();
    }

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateSize();

    private void UpdateSize()
    {
        EditorRoot.Width = Math.Max(0, Math.Min(940, XamlRoot.Size.Width - 96));
        // Reserve space for the native dialog's title, footer and content margins.
        EditorRoot.Height = Math.Max(0, Math.Min(560, XamlRoot.Size.Height - 260));
    }

    private bool IsModified => _document?.IsModified(EditorText.Text) == true;

    private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            var bytes = await _service.ReadTextFileAsync(_path, _token);
            var document = await Task.Run(() => TextFileDocument.Decode(bytes), _token);
            if (_closed) return;
            _document = document;
            EditorText.Text = document.Text;
            EditorText.Focus(FocusState.Programmatic);
            if (document.HasMixedNewLines)
                ShowNotice($"文件包含混合换行符，修改后保存将统一为 {document.NewLineLabel}。", InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closed) ShowNotice($"读取失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (!_closed) { LoadingRing.IsActive = false; UpdateState(); }
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (_saving || _document is null || !IsModified || _closed) return false;
        var document = _document;
        var text = EditorText.Text;
        _saving = true;
        LoadingRing.IsActive = true;
        Notice.IsOpen = false;
        UpdateState();
        try
        {
            var bytes = await Task.Run(() => document.Encode(text), _token);
            await _service.SaveTextFileAsync(_path, document.OriginalBytes, bytes, _token);
            document.AcceptSaved(text, bytes);
            WasSaved = true;
            ShowNotice(_remote ? "已保存到远程文件。" : "已保存到本地文件。", InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            ShowNotice($"保存失败：{exception.Message}", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            _saving = false;
            LoadingRing.IsActive = false;
            UpdateState();
        }
    }

    /// <summary>同时用于对话框和主窗口关闭，未保存修改始终留在编辑器中。</summary>
    public bool TryClose()
    {
        if (!CanClose()) return false;
        Hide();
        return true;
    }

    private bool CanClose()
    {
        if (_saving)
        {
            ShowNotice("正在保存，请等待保存完成后关闭。", InfoBarSeverity.Informational);
            return false;
        }
        if (!_discard && IsModified)
        {
            ShowNotice("有未保存的修改。请保存，或选择放弃修改并关闭。", InfoBarSeverity.Warning, true);
            return false;
        }
        return true;
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = !CanClose();
    private async void Dialog_SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await SaveAsync();
    }
    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveAsync();
    }
    private void Find_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }
    private void Discard_Click(object sender, RoutedEventArgs args)
    {
        if (_saving) return;
        _discard = true;
        Hide();
    }
    private void FindNext_Click(object sender, RoutedEventArgs args) => FindNext();
    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter) return;
        args.Handled = true;
        FindNext();
    }
    private void FindNext()
    {
        if (_document is null || string.IsNullOrEmpty(FindBox.Text)) return;
        var text = EditorText.Text;
        var start = Math.Min(text.Length, EditorText.SelectionStart + EditorText.SelectionLength);
        var index = text.IndexOf(FindBox.Text, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(FindBox.Text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { ShowNotice("未找到匹配的文本。", InfoBarSeverity.Informational); return; }
        Notice.IsOpen = false;
        EditorText.Focus(FocusState.Programmatic);
        EditorText.Select(index, FindBox.Text.Length);
    }
    private void ReadOnly_Changed(object sender, RoutedEventArgs args) { if (EditorText is not null) UpdateState(); }
    private void Wrap_Changed(object sender, RoutedEventArgs args)
    {
        if (EditorText is null) return;
        var wrap = WrapBox.IsChecked == true;
        EditorText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(EditorText, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }
    private void Editor_TextChanged(object sender, TextChangedEventArgs args) { if (FileStatus is not null) UpdateState(); }
    private void Editor_SelectionChanged(object sender, RoutedEventArgs args) { if (CaretStatus is not null) UpdateCaret(); }
    private void UpdateState()
    {
        var ready = _document is not null;
        EditorText.IsReadOnly = !ready || _saving || ReadOnlyBox.IsChecked == true;
        ReadOnlyBox.IsEnabled = ready && !_saving;
        IsPrimaryButtonEnabled = !_saving && IsModified;
        DiscardButton.IsEnabled = !_saving;
        Title = _name + (IsModified ? " · 未保存" : "");
        FileStatus.Text = _saving ? "正在保存…" : _document is { } document
            ? $"{document.EncodingLabel} · {document.NewLineLabel} · {(IsModified ? "已修改" : "未修改")} · Ctrl+S 保存"
            : LoadingRing.IsActive ? "正在读取…" : "未打开文件";
        UpdateCaret();
    }
    private void UpdateCaret()
    {
        var text = EditorText.Text.AsSpan(0, Math.Min(EditorText.SelectionStart, EditorText.Text.Length));
        var line = 1;
        var column = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { line++; column = 1; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (text[i] == '\n') { line++; column = 1; }
            else column++;
        }
        CaretStatus.Text = $"行 {line}，列 {column}";
    }
    private void ShowNotice(string message, InfoBarSeverity severity, bool discard = false)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        DiscardButton.Visibility = discard ? Visibility.Visible : Visibility.Collapsed;
        Notice.IsOpen = true;
    }
}
