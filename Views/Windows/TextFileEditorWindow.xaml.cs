using System.Text;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIEditor;

namespace FluentShell.Views.Session;

public sealed partial class TextFileEditorWindow : Window
{
    private readonly ITextFileService _service;
    private readonly string _path;
    private readonly string _name;
    private readonly bool _remote;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FrameworkElement? _ownerRoot;
    private readonly long _ownerBackdropToken;
    private bool _editorPreferencesReady;
    private TextFileDocument? _document;
    private bool _saving;
    private bool _closed;
    private bool _started;
    private bool _discard;
    private int _lineNumberMarginWidth;
    private bool _lineNumberUpdatePending;
    private XamlRoot? _editorXamlRoot;

    public Task Completion => _completion.Task;
    public bool WasSaved { get; private set; }
    public string FilePath => _path;
    public bool IsRemote => _remote;
    private bool IsModified => _document is not null && CodeEditor.Editor.Modify;

    public TextFileEditorWindow(ITextFileService service, string path, string name, bool remote, XamlRoot ownerRoot, IntPtr ownerHandle)
    {
        _service = service;
        _path = path;
        _name = name;
        _remote = remote;
        _token = _lifetime.Token;
        InitializeComponent();
        _ownerRoot = ownerRoot.Content as FrameworkElement;
        if (_ownerRoot is not null)
        {
            _ownerRoot.ActualThemeChanged += OwnerTheme_Changed;
            _ownerBackdropToken = _ownerRoot.RegisterPropertyChangedCallback(WindowBackdrop.MaterialProperty, OwnerBackdrop_Changed);
        }
        ApplyTheme();
        ApplyBackdrop();
        LoadEditorPreferences();
        RootGrid.ActualThemeChanged += RootTheme_Changed;
        PathText.Text = $"{(remote ? "远程文件" : "本地文件")} · {path}";
        ToolTipService.SetToolTip(PathText, path);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(EditorTitleBar);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "FluentShell.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var owner = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(ownerHandle));
        var area = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = ownerRoot.RasterizationScale;
        var width = Math.Min((int)(1000 * scale), area.Width);
        var height = Math.Min((int)(720 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(
            Math.Clamp(owner.Position.X + (owner.Size.Width - width) / 2, area.X, area.X + area.Width - width),
            Math.Clamp(owner.Position.Y + (owner.Size.Height - height) / 2, area.Y, area.Y + area.Height - height), width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(620 * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(360 * scale), area.Height);
        }
        AppWindow.Closing += Window_Closing;
        Closed += Window_Closed;
        CodeEditor.Editor.ReadOnly = true;
        CodeEditor.Editor.Modified += Editor_Modified;
        CodeEditor.Editor.UpdateUI += Editor_UpdateUI;
        CodeEditor.Editor.ZoomChanged += Editor_ZoomChanged;
        CodeEditor.HighlightingLanguage = LanguageFor(name);
        UpdateState();
    }

    private static string LanguageFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".c" or ".h" or ".cc" or ".cpp" or ".hpp" => "cpp",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".json" or ".jsonc" => "json",
        ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".svg" => "xml",
        ".html" or ".htm" => "html",
        ".yaml" or ".yml" => "yaml",
        _ => ""
    };

    private void OwnerTheme_Changed(FrameworkElement sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_closed) ApplyTheme(); });
    private void OwnerBackdrop_Changed(DependencyObject sender, DependencyProperty property) => DispatcherQueue.TryEnqueue(() => { if (!_closed) ApplyBackdrop(); });
    private void ApplyBackdrop() => WindowBackdrop.Apply(this, _ownerRoot is null ? "Mica" : WindowBackdrop.GetMaterial(_ownerRoot));
    private void RootTheme_Changed(FrameworkElement sender, object args) => WindowChrome.ApplyTitleBarColors(AppWindow, RootGrid.ActualTheme, "系统");
    private void ApplyTheme()
    {
        if (_ownerRoot is not null) RootGrid.RequestedTheme = _ownerRoot.ActualTheme;
        WindowChrome.ApplyTitleBarColors(AppWindow, RootGrid.ActualTheme, "系统");
    }

    private void LoadEditorPreferences()
    {
        var preferences = TextEditorSettings.GetPreferences(_ownerRoot);
        ReadOnlyBox.IsChecked = preferences.ReadOnly;
        WrapBox.IsChecked = preferences.WordWrap;
        LineNumbersBox.IsChecked = preferences.ShowLineNumbers;
        WhitespaceBox.IsChecked = preferences.ShowWhitespace;
        IndentWidthBox.SelectedIndex = preferences.IndentWidth switch { 2 => 0, 8 => 2, _ => 1 };
        UseTabsBox.IsChecked = preferences.UseTabs;
        _editorPreferencesReady = true;
    }

    private async void SaveEditorPreferences()
    {
        if (!_editorPreferencesReady || _closed) return;
        var preferences = new TextEditorPreferences
        {
            ReadOnly = ReadOnlyBox.IsChecked == true, WordWrap = WrapBox.IsChecked == true,
            ShowLineNumbers = LineNumbersBox.IsChecked == true, ShowWhitespace = WhitespaceBox.IsChecked == true,
            IndentWidth = IndentWidthBox.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 }, UseTabs = UseTabsBox.IsChecked == true
        };
        try { await TextEditorSettings.SaveAsync(_ownerRoot, preferences); }
        catch (Exception exception)
        {
            DiagnosticLog.Record("TextEditorSettingsSaveFailed");
            if (!_closed) ShowNotice($"编辑器设置保存失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        if (_started) return;
        _started = true;
        _editorXamlRoot = RootGrid.XamlRoot;
        _editorXamlRoot.Changed += EditorRoot_Changed;
        try
        {
            var bytes = await _service.ReadTextFileAsync(_path, _token);
            var document = await Task.Run(() => TextFileDocument.Decode(bytes), _token);
            if (_closed) return;
            var editor = CodeEditor.Editor;
            editor.CodePage = 65001;
            editor.EOLMode = EndOfLine.Lf;
            ApplyIndentationSettings();
            editor.ReadOnly = false;
            editor.SetText(document.Text);
            editor.EmptyUndoBuffer();
            editor.SetSavePoint();
            _document = document;
            ApplyLineNumberVisibility();
            CodeEditor.Focus(FocusState.Programmatic);
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

    private string GetDocumentText()
    {
        // Scintilla lengths and positions are UTF-8 bytes. Its GetText buffer includes a terminator.
        var text = CodeEditor.Editor.GetText(CodeEditor.Editor.Length + 1);
        return text.EndsWith('\0') ? text[..^1] : text;
    }

    public async Task<bool> SaveAsync()
    {
        if (_saving || _document is null || !IsModified || _closed) return false;
        var document = _document;
        var text = GetDocumentText();
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
            if (!_closed)
            {
                CodeEditor.Editor.SetSavePoint();
                ShowNotice(_remote ? "已保存到远程文件。" : "已保存到本地文件。", InfoBarSeverity.Success);
            }
            return true;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (!_closed) ShowNotice($"保存失败：{exception.Message}", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            _saving = false;
            if (!_closed) { LoadingRing.IsActive = false; UpdateState(); }
        }
    }

    public bool TryClose()
    {
        if (_closed) return true;
        if (!CanClose()) { Activate(); return false; }
        Close();
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
    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args) => args.Cancel = !CanClose();
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        AppWindow.Closing -= Window_Closing;
        if (_ownerRoot is not null)
        {
            _ownerRoot.ActualThemeChanged -= OwnerTheme_Changed;
            _ownerRoot.UnregisterPropertyChangedCallback(WindowBackdrop.MaterialProperty, _ownerBackdropToken);
        }
        RootGrid.ActualThemeChanged -= RootTheme_Changed;
        CodeEditor.Editor.Modified -= Editor_Modified;
        CodeEditor.Editor.UpdateUI -= Editor_UpdateUI;
        CodeEditor.Editor.ZoomChanged -= Editor_ZoomChanged;
        if (_editorXamlRoot is not null) _editorXamlRoot.Changed -= EditorRoot_Changed;
        _completion.TrySetResult();
    }

    private async void Save_Click(object sender, RoutedEventArgs args) => await SaveAsync();
    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await SaveAsync(); }
    private void Find_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; ShowFind(); }
    private void Close_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; TryClose(); }
    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FindPanel.Visibility != Visibility.Visible) return;
        args.Handled = true;
        HideFind();
    }
    private void Discard_Click(object sender, RoutedEventArgs args) { if (_saving) return; _discard = true; TryClose(); }
    private void Undo_Click(object sender, RoutedEventArgs args) { if (!CodeEditor.Editor.ReadOnly) CodeEditor.Editor.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs args) { if (!CodeEditor.Editor.ReadOnly) CodeEditor.Editor.Redo(); }
    private void Find_Click(object sender, RoutedEventArgs args) => ShowFind();
    private void ShowFind() { FindPanel.Visibility = Visibility.Visible; FindBox.Focus(FocusState.Programmatic); FindBox.SelectAll(); }
    private void HideFind_Click(object sender, RoutedEventArgs args) => HideFind();
    private void HideFind() { FindPanel.Visibility = Visibility.Collapsed; CodeEditor.Focus(FocusState.Programmatic); }
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
        var editor = CodeEditor.Editor;
        var start = editor.SelectionEnd;
        editor.SearchFlags = FindOption.None;
        editor.TargetStart = start;
        editor.TargetEnd = editor.Length;
        var length = Encoding.UTF8.GetByteCount(FindBox.Text);
        var found = editor.SearchInTarget(length, FindBox.Text);
        if (found < 0)
        {
            editor.TargetStart = 0;
            editor.TargetEnd = start;
            found = editor.SearchInTarget(length, FindBox.Text);
        }
        if (found < 0) { ShowNotice("未找到匹配的文本。", InfoBarSeverity.Informational); return; }
        Notice.IsOpen = false;
        editor.SetSel(editor.TargetStart, editor.TargetEnd);
        editor.ScrollCaret();
        CodeEditor.Focus(FocusState.Programmatic);
    }
    private void ReadOnly_Changed(object sender, RoutedEventArgs args)
    {
        if (CodeEditor is not null && FileStatus is not null) UpdateState();
        SaveEditorPreferences();
    }
    private void Wrap_Changed(object sender, RoutedEventArgs args)
    {
        if (CodeEditor is not null) CodeEditor.Editor.WrapMode = WrapBox.IsChecked == true ? Wrap.Word : Wrap.None;
        SaveEditorPreferences();
    }
    private void LineNumbers_Changed(object sender, RoutedEventArgs args)
    {
        if (CodeEditor is not null) ApplyLineNumberVisibility();
        SaveEditorPreferences();
    }
    private void ApplyLineNumberVisibility()
    {
        var editor = CodeEditor.Editor;
        var width = editor.GetMarginWidthN(0);
        if (LineNumbersBox.IsChecked == true)
        {
            if (width == 0 && _lineNumberMarginWidth > 0) editor.SetMarginWidthN(0, _lineNumberMarginWidth);
        }
        else if (width > 0)
        {
            // WinUIEdit recalculates this margin on line-count, DPI and zoom changes.
            _lineNumberMarginWidth = width;
            editor.SetMarginWidthN(0, 0);
        }
    }
    private void Editor_ZoomChanged(Editor sender, ZoomChangedEventArgs args) => ScheduleLineNumberVisibility();
    private void EditorRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ScheduleLineNumberVisibility();
    private void ScheduleLineNumberVisibility()
    {
        if (_closed || _lineNumberUpdatePending) return;
        _lineNumberUpdatePending = true;
        // Native notifications reach the C# wrapper before WinUIEdit recalculates its margins.
        // Apply the preference after that native notification has finished.
        DispatcherQueue.TryEnqueue(() =>
        {
            _lineNumberUpdatePending = false;
            if (!_closed) ApplyLineNumberVisibility();
        });
    }
    private void Whitespace_Changed(object sender, RoutedEventArgs args)
    {
        if (CodeEditor is not null) CodeEditor.Editor.ViewWS = WhitespaceBox.IsChecked == true ? WhiteSpace.VisibleAlways : WhiteSpace.Invisible;
        SaveEditorPreferences();
    }
    private void IndentWidth_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (CodeEditor is not null) ApplyIndentationSettings();
        SaveEditorPreferences();
    }
    private void UseTabs_Changed(object sender, RoutedEventArgs args)
    {
        if (CodeEditor is not null) ApplyIndentationSettings();
        SaveEditorPreferences();
    }
    private void ApplyIndentationSettings()
    {
        var width = IndentWidthBox.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 };
        CodeEditor.Editor.TabWidth = width;
        CodeEditor.Editor.Indent = width;
        CodeEditor.Editor.UseTabs = UseTabsBox.IsChecked == true;
    }
    private void Editor_Modified(Editor sender, ModifiedEventArgs args)
    {
        if (args.LinesAdded != 0) ScheduleLineNumberVisibility();
        if (!_closed && (args.ModificationType & (int)(ModificationFlags.InsertText | ModificationFlags.DeleteText)) != 0) UpdateState();
    }
    private void Editor_UpdateUI(Editor sender, UpdateUIEventArgs args)
    {
        if (_closed) return;
        ApplyLineNumberVisibility();
        UpdateState();
    }
    private void UpdateState()
    {
        var editor = CodeEditor.Editor;
        var ready = _document is not null;
        editor.ReadOnly = !ready || _saving || ReadOnlyBox.IsChecked == true;
        ReadOnlyBox.IsEnabled = ready && !_saving;
        SaveButton.IsEnabled = !_saving && IsModified;
        UndoButton.IsEnabled = !editor.ReadOnly && editor.CanUndo();
        RedoButton.IsEnabled = !editor.ReadOnly && editor.CanRedo();
        DiscardButton.IsEnabled = !_saving;
        Title = _name + (IsModified ? " · 未保存" : "") + " — FluentShell";
        WindowTitle.Text = Title;
        FileStatus.Text = _saving ? "正在保存…" : _document is { } document
            ? $"{document.EncodingLabel} · {document.NewLineLabel} · {(IsModified ? "已修改" : "未修改")}"
            : LoadingRing.IsActive ? "正在读取…" : "未打开文件";
        CaretStatus.Text = $"行 {editor.LineFromPosition(editor.CurrentPos) + 1}，列 {editor.GetColumn(editor.CurrentPos) + 1}";
    }
    private void ShowNotice(string message, InfoBarSeverity severity, bool discard = false)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        DiscardButton.Visibility = discard ? Visibility.Visible : Visibility.Collapsed;
        Notice.IsOpen = true;
    }
}
