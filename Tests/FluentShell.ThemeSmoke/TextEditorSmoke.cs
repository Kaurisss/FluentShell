using System.Reflection;
using System.Text;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Session;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEditor;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private static T EditorElement<T>(TextFileEditorWindow window, string name) => (T)((Grid)window.Content).FindName(name);
    private static void EditorCommand(TextFileEditorWindow window, string name) =>
        typeof(TextFileEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void ReplaceDocument(CodeEditorControl control, string text)
    {
        control.Editor.SetSel(0, control.Editor.Length);
        control.Editor.ReplaceSel(text);
    }
    private static void DiscardEditor(TextFileEditorWindow editor) =>
        typeof(TextFileEditorWindow).GetMethod("Discard_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { editor, new RoutedEventArgs() });

    private static async Task EditorUiAsync(TextFileEditorWindow window, string command, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("winapp")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("ui");
        start.ArgumentList.Add(command);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--window");
        start.ArgumentList.Add(WinRT.Interop.WindowNative.GetWindowHandle(window).ToString());
        start.ArgumentList.Add("--json");
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var result = await output;
        var diagnostic = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"winapp ui {command}: {result} {diagnostic}");
    }

    private async Task VerifyTextEditorAsync()
    {
        _window = new Window { Title = "FluentShell offline WinUIEdit regression" };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        var root = new Grid { RequestedTheme = ElementTheme.Light };
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var view = new SftpWorkspaceView(handle);
        root.Children.Add(view);
        _window.Content = root;
        _window.Activate();
        var nativeUi = Environment.GetCommandLineArgs().Contains("--native-editor-ui");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(nativeUi ? 120 : 55));
        async Task WaitFor(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
        void Check(bool passed, string description)
        {
            Program.Results.Add(new { control = description, passed });
            if (!passed) throw new InvalidOperationException(description);
        }
        List<TextFileEditorWindow> Editors() => Field<List<TextFileEditorWindow>>(view, "_textEditors");
        await WaitFor(() => view.XamlRoot is not null);
        var path = Path.GetTempFileName();
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                const string original = "{\r\n  \"标题\": \"中文配置😀\",\r\n  \"key\": \"value\",\r\n  \"enabled\": true\r\n}\r\n";
                await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(original));
                var table = (Syncfusion.UI.Xaml.DataGrid.SfDataGrid)view.FindName("LocalFiles");
                await WaitFor(() => table.IsEnabled && Field<string?>(view, "_localPath") is not null);
                var items = Field<System.Collections.ObjectModel.ObservableCollection<SftpWorkspaceView.LocalPaneItem>>(view, "_localFiles");
                var item = new SftpWorkspaceView.LocalPaneItem("config.json", path, false, 100, DateTime.Now);
                items.Add(item);
                table.SelectedItem = item;
                Task OpenLocal() => (Task)typeof(SftpWorkspaceView).GetMethod("OpenLocalItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
                var showing = OpenLocal();
                await WaitFor(() => Editors().Count == 1);
                var editor = Editors().Single();
                var text = EditorElement<CodeEditorControl>(editor, "CodeEditor");
                await WaitFor(() => !EditorElement<ProgressRing>(editor, "LoadingRing").IsActive);
                var surface = (Grid)editor.Content;
                var current = (string)typeof(TextFileEditorWindow).GetMethod("GetDocumentText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null)!;
                Check(current == TextFileDocument.NormalizeNewLines(original), "UTF-8 Chinese and emoji round trip without a trailing NUL");
                Check(surface.ActualTheme == theme && text.ActualWidth > 600 && text.ActualHeight > 200 && text.Editor.ReadOnly,
                    theme + ": native editor opens read-only and fills a responsive window");
                Check(editor.SystemBackdrop is MicaBackdrop && ((SolidColorBrush)surface.Background).Color.A == 0 &&
                    ((SolidColorBrush)text.Background).Color.A == 0, "Mica window and transparent native editor surface");
                Check(text.HighlightingLanguage == "json" && text.Editor.GetMarginWidthN(0) > 0, "JSON syntax highlighting and native line numbers enabled");
                Check(surface.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden &&
                    surface.KeyboardAccelerators.Count == 4, "Root accelerator tooltip is hidden without disabling shortcuts");
                var settings = EditorElement<Button>(editor, "SettingsButton");
                Check(ReferenceEquals(settings.Parent, EditorElement<StackPanel>(editor, "EditorToolbar")) && settings.Flyout is Flyout,
                    "Editor settings button belongs to the command StackPanel");
                settings.Flyout.ShowAt(settings);
                await Task.Delay(100, timeout.Token);
                if (nativeUi) await EditorUiAsync(editor, "screenshot", "--output", Program.ReportPath + ".settings." + theme + ".png", "--capture-screen");
                var readonlyBox = EditorElement<CheckBox>(editor, "ReadOnlyBox");
                var wrapBox = EditorElement<CheckBox>(editor, "WrapBox");
                Check(Descendants(((Flyout)settings.Flyout).Content).Contains(readonlyBox) &&
                    Descendants(((Flyout)settings.Flyout).Content).Contains(wrapBox), "Read-only and wrap options are inside the settings flyout");
                var whitespace = EditorElement<CheckBox>(editor, "WhitespaceBox");
                whitespace.IsChecked = true;
                Check(text.Editor.ViewWS == WhiteSpace.VisibleAlways, "Settings reveal whitespace in the native editor");
                whitespace.IsChecked = false;
                var indent = EditorElement<ComboBox>(editor, "IndentWidthBox");
                var tabs = EditorElement<CheckBox>(editor, "UseTabsBox");
                indent.SelectedIndex = 0;
                tabs.IsChecked = true;
                Check(text.Editor.Indent == 2 && text.Editor.TabWidth == 2 && text.Editor.UseTabs, "Settings apply Tab indentation and width");
                indent.SelectedIndex = 1;
                tabs.IsChecked = false;
                Check(text.Editor.Indent == 4 && !text.Editor.UseTabs && !text.Editor.Modify, "Display and indentation settings preserve document content");
                var lineNumbers = EditorElement<CheckBox>(editor, "LineNumbersBox");
                lineNumbers.IsChecked = false;
                text.Editor.Zoom = 2;
                await WaitFor(() => text.Editor.GetMarginWidthN(0) == 0);
                Check(true, "Hidden line numbers remain hidden after zoom changes");
                lineNumbers.IsChecked = true;
                Check(text.Editor.GetMarginWidthN(0) > 0, "Line numbers can be restored");
                text.Editor.Zoom = 0;
                settings.Flyout.Hide();
                Check(text.Focus(FocusState.Programmatic), "Native editor accepts keyboard focus");
                if (nativeUi)
                {
                    await EditorUiAsync(editor, "hover", "TextEditorTitle", "--dwell-time", "1200");
                    Check(!VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot).SelectMany(p => new[] { p.Child }.Concat(Descendants(p.Child)))
                        .OfType<ToolTip>().Any(t => t.IsOpen), "Native title-bar hover does not open an accelerator tooltip");
                }
                await OpenLocal();
                Check(Editors().Count == 1, "Opening the same file activates the existing editor");
                root.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
                await WaitFor(() => surface.ActualTheme == root.ActualTheme && text.ActualTheme == root.ActualTheme);
                Check(true, "Open editor follows the owner theme change");
                root.RequestedTheme = theme;
                await WaitFor(() => text.ActualTheme == theme);
                EditorElement<CheckBox>(editor, "ReadOnlyBox").IsChecked = false;
                Check(!text.Editor.ReadOnly, "Read-only toggle enables native editing");
                EditorElement<CheckBox>(editor, "WrapBox").IsChecked = true;
                Check(text.Editor.WrapMode == Wrap.Word, "Native word wrapping enabled");
                if (nativeUi)
                {
                    await EditorUiAsync(editor, "send-keys", "ctrl+f", "--via", "send-input", "--target", "TextEditorContent");
                    await WaitFor(() => EditorElement<Grid>(editor, "FindPanel").Visibility == Visibility.Visible);
                    Check(true, "Ctrl+F opens find while the native editor has focus");
                }
                else EditorCommand(editor, "ShowFind");
                var find = EditorElement<TextBox>(editor, "FindBox");
                find.Text = "中文配置😀";
                text.Editor.SetSel(0, 0);
                EditorCommand(editor, "FindNext");
                Check(text.Editor.GetSelText().TrimEnd('\0') == find.Text && text.Editor.SelectionEnd - text.Editor.SelectionStart == Encoding.UTF8.GetByteCount(find.Text),
                    "Find selects Chinese and emoji using UTF-8 byte positions");
                text.Editor.SetSel(text.Editor.Length, text.Editor.Length);
                EditorCommand(editor, "FindNext");
                Check(text.Editor.GetSelText().TrimEnd('\0') == find.Text, "Find wraps at the end of the document");
                EditorCommand(editor, "HideFind");
                lineNumbers.IsChecked = false;
                ReplaceDocument(text, TextFileDocument.NormalizeNewLines(original).Replace("value", "edited"));
                await WaitFor(() => EditorElement<Button>(editor, "SaveButton").IsEnabled);
                await WaitFor(() => text.Editor.GetMarginWidthN(0) == 0);
                Check(true, "Hidden line numbers remain hidden after document edits");
                lineNumbers.IsChecked = true;
                text.Editor.Undo();
                await WaitFor(() => !EditorElement<Button>(editor, "SaveButton").IsEnabled);
                Check(!text.Editor.Modify, "Undo restores the loaded save point");
                text.Editor.Redo();
                await WaitFor(() => EditorElement<Button>(editor, "SaveButton").IsEnabled);
                Check(!view.TryCloseTextEditor() && !showing.IsCompleted, "Workspace close preserves the unsaved draft");
                SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(editor), 0x0010u, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(80, timeout.Token);
                Check(!editor.Completion.IsCompleted && EditorElement<Button>(editor, "DiscardButton").Visibility == Visibility.Visible,
                    "Native window close preserves dirty text and offers explicit discard");
                if (nativeUi)
                {
                    await EditorUiAsync(editor, "send-keys", "ctrl+s", "--via", "send-input", "--target", "TextEditorContent");
                    await WaitFor(() => editor.WasSaved && !EditorElement<ProgressRing>(editor, "LoadingRing").IsActive);
                    Check(true, "Ctrl+S saves while the native editor has focus");
                }
                else Check(await editor.SaveAsync(), "Production local service saves native editor content");
                Check(!EditorElement<Button>(editor, "SaveButton").IsEnabled, "Save resets the native save point");
                Check(await File.ReadAllTextAsync(path) == original.Replace("value", "edited"), "Save retains CRLF without appending a NUL");
                text.Editor.SetSel(0, 0);
                surface.UpdateLayout();
                await Task.Delay(120, timeout.Token);
                if (nativeUi) await EditorUiAsync(editor, "screenshot", "--output", Program.ReportPath + "." + theme + ".png", "--capture-screen");
                else await CaptureAsync(surface, Program.ReportPath + "." + theme + ".png");
                Check(EditorElement<TextBlock>(editor, "FileStatus").ActualHeight >= 12, "Encoding and newline status remains visible");
                ReplaceDocument(text, "unsaved draft");
                await File.WriteAllTextAsync(path, "external content");
                Check(!await editor.SaveAsync() && text.Editor.Modify, "Conflicting save retains the draft");
                Check(await File.ReadAllTextAsync(path) == "external content", "Conflicting save preserves external content");
                DiscardEditor(editor);
                await showing.WaitAsync(timeout.Token);
            }
            await VerifyRemoteTextEditorAsync(root, view, handle, timeout.Token, Check, WaitFor, Editors);
        }
        finally { File.Delete(path); }
        _window.Close();
        Program.Finish();
    }

    private async Task VerifyRemoteTextEditorAsync(Grid root, SftpWorkspaceView view, IntPtr handle, CancellationToken token,
        Action<bool, string> check, Func<Func<bool>, Task> waitFor, Func<List<TextFileEditorWindow>> editors)
    {
        var remote = new SyntheticTextService();
        view.SetTextFileService(remote);
        var item = new RemoteFileItem { Name = "remote.txt", FullPath = "/untrusted/remote.txt", SizeBytes = 8 };
        view.Render(new(SftpSessionState.Idle, new("/", [item]), true, true, true, "", null));
        var table = (Syncfusion.UI.Xaml.DataGrid.SfDataGrid)view.FindName("RemoteTable");
        table.SelectedItem = item;
        typeof(SftpWorkspaceView).GetMethod("OpenSelectedRemoteText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        await waitFor(() => editors().Count == 1);
        var editor = editors().Single();
        await waitFor(() => !EditorElement<ProgressRing>(editor, "LoadingRing").IsActive);
        check(remote.ReadPath == "/remote.txt", "Remote entry point derives path from validated name and current directory");
        EditorElement<CheckBox>(editor, "ReadOnlyBox").IsChecked = false;
        var text = EditorElement<CodeEditorControl>(editor, "CodeEditor");
        ReplaceDocument(text, "saved remotely");
        await waitFor(() => EditorElement<Button>(editor, "SaveButton").IsEnabled);
        remote.SaveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = editor.SaveAsync();
        await waitFor(() => remote.SavePath is not null);
        check(!editor.TryClose() && text.Editor.ReadOnly, "Saving blocks native close and edits until completion");
        var second = new RemoteFileItem { Name = "second.yaml", FullPath = "/second.yaml", SizeBytes = 8 };
        view.Render(new(SftpSessionState.Idle, new("/", [item, second]), true, true, true, "", null));
        table.SelectedItem = second;
        typeof(SftpWorkspaceView).GetMethod("OpenSelectedRemoteText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        await waitFor(() => editors().Count == 2);
        var other = editors().Single(e => !ReferenceEquals(e, editor));
        check(true, "Different files open in independent native windows");
        check(other.TryClose(), "Independent clean editor closes");
        remote.SaveCompletion.SetResult();
        check(await saving && remote.SavePath == "/remote.txt" && Encoding.UTF8.GetString(remote.Content) == "saved remotely",
            "Remote save uses captured path and completes before closing");
        check(editor.TryClose(), "Clean remote editor closes");
        await waitFor(() => editors().Count == 0);

        var delayed = new SyntheticTextService { ReadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var lateEditor = new TextFileEditorWindow(delayed, "/late.txt", "late.txt", true, root.XamlRoot, handle);
        lateEditor.Activate();
        await waitFor(() => delayed.ReadPath is not null);
        check(lateEditor.TryClose(), "Loading editor can be cancelled");
        await lateEditor.Completion.WaitAsync(token);
        check(delayed.ReadToken.IsCancellationRequested, "Closing loading editor cancels the read");
        delayed.ReadCompletion.SetResult();
        await Task.Delay(100, token);
        check(Field<TextFileDocument?>(lateEditor, "_document") is null, "Late read does not mutate a closed editor");

        var binary = new SyntheticTextService { Content = [0, 1, 2] };
        var failedEditor = new TextFileEditorWindow(binary, "/binary", "binary", true, root.XamlRoot, handle);
        failedEditor.Activate();
        await waitFor(() => EditorElement<InfoBar>(failedEditor, "Notice").IsOpen);
        check(!EditorElement<Button>(failedEditor, "SaveButton").IsEnabled && EditorElement<InfoBar>(failedEditor, "Notice").Severity == InfoBarSeverity.Error,
            "Binary read failure is visible and cannot be saved");
        failedEditor.TryClose();
        await failedEditor.Completion.WaitAsync(token);
    }

    private sealed class SyntheticTextService : ITextFileService
    {
        public byte[] Content = Encoding.UTF8.GetBytes("original");
        public string? ReadPath;
        public string? SavePath;
        public CancellationToken ReadToken;
        public TaskCompletionSource? SaveCompletion;
        public TaskCompletionSource? ReadCompletion;
        public async Task<byte[]> ReadTextFileAsync(string path, CancellationToken cancellationToken)
        {
            ReadPath = path;
            ReadToken = cancellationToken;
            if (ReadCompletion is not null) await ReadCompletion.Task;
            return Content.ToArray();
        }
        public async Task SaveTextFileAsync(string path, ReadOnlyMemory<byte> expected, byte[] content, CancellationToken cancellationToken)
        {
            SavePath = path;
            if (SaveCompletion is not null) await SaveCompletion.Task;
            if (!expected.Span.SequenceEqual(Content)) throw new IOException("External modification.");
            Content = content.ToArray();
        }
    }
}
