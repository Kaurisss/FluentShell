using System.Reflection;
using System.Text;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Session;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifyTextEditorAsync()
    {
        _window = new Window { Title = "FluentShell offline text editor regression" };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        var root = new Grid { RequestedTheme = ElementTheme.Light };
        var view = new SftpWorkspaceView(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        root.Children.Add(view);
        _window.Content = root;
        _window.Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        async Task WaitFor(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
        void Check(bool passed, string description)
        {
            Program.Results.Add(new { control = description, passed });
            if (!passed) throw new InvalidOperationException(description);
        }
        TextFileEditorDialog Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .SelectMany(p => new[] { p.Child }.Concat(Descendants(p.Child))).OfType<TextFileEditorDialog>().Single();
        await WaitFor(() => view.XamlRoot is not null);
        var path = Path.GetTempFileName();
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("中文配置\r\nkey=value\r\n"));
                var table = (Syncfusion.UI.Xaml.DataGrid.SfDataGrid)view.FindName("LocalFiles");
                await WaitFor(() => table.IsEnabled && Field<string?>(view, "_localPath") is not null);
                var localItems = Field<System.Collections.ObjectModel.ObservableCollection<SftpWorkspaceView.LocalPaneItem>>(view, "_localFiles");
                var localItem = new SftpWorkspaceView.LocalPaneItem("config.txt", path, false, 32, DateTime.Now);
                localItems.Add(localItem);
                table.SelectedItem = localItem;
                var showing = (Task)typeof(SftpWorkspaceView).GetMethod("OpenLocalItemAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
                await Task.Delay(150, timeout.Token);
                var dialog = Dialog();
                var text = (TextBox)dialog.FindName("EditorText");
                await WaitFor(() => !((ProgressRing)dialog.FindName("LoadingRing")).IsActive);
                Check(dialog.ActualTheme == theme && dialog.ActualWidth > 600 && text.ActualHeight > 200 && text.IsReadOnly,
                    theme + ": responsive editor opens in read-only mode");
                Check(TextFileDocument.NormalizeNewLines(text.Text) == "中文配置\nkey=value\n", "Local entry point loads real temporary text file");
                Check(text.Focus(FocusState.Programmatic), "Editor accepts keyboard focus");
                ((CheckBox)dialog.FindName("ReadOnlyBox")).IsChecked = false;
                Check(!text.IsReadOnly, "Read-only toggle enables editing");
                ((CheckBox)dialog.FindName("WrapBox")).IsChecked = true;
                Check(text.TextWrapping == TextWrapping.Wrap, "Automatic wrapping enables native text wrapping");
                ((TextBox)dialog.FindName("FindBox")).Text = "value";
                typeof(TextFileEditorDialog).GetMethod("FindNext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
                Check(text.SelectedText == "value", "Find command selects a matching range");
                text.Text = "中文配置\rkey=edited\r";
                await WaitFor(() => dialog.IsPrimaryButtonEnabled);
                Check(dialog.IsPrimaryButtonEnabled && !view.TryCloseTextEditor() && !showing.IsCompleted,
                    "Dirty editor blocks window close and keeps content");
                Check(((Button)dialog.FindName("DiscardButton")).Visibility == Visibility.Visible, "Dirty close offers explicit discard");
                Check(await dialog.SaveAsync(), "Save succeeds through the production local service");
                Check(!dialog.IsPrimaryButtonEnabled, "Successful save clears the dirty state");
                Check(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)) == "中文配置\r\nkey=edited\r\n", "Saved file retains CRLF");
                dialog.UpdateLayout();
                var status = (TextBlock)dialog.FindName("FileStatus");
                var statusBottom = status.TransformToVisual(dialog).TransformPoint(new Windows.Foundation.Point(0, status.ActualHeight)).Y;
                Check(status.ActualHeight >= 12 && statusBottom <= dialog.ActualHeight - 74,
                    "Encoding and newline status fits above the dialog footer");
                await CaptureAsync(dialog, Program.ReportPath + "." + theme + ".png");
                text.Text = "unsaved draft";
                await File.WriteAllTextAsync(path, "external content");
                Check(!await dialog.SaveAsync() && dialog.IsPrimaryButtonEnabled && text.Text == "unsaved draft", "Conflicting save retains the draft");
                Check(await File.ReadAllTextAsync(path) == "external content", "Conflicting save leaves external content intact");
                dialog.Hide();
                await Task.Delay(80, timeout.Token);
                Check(!showing.IsCompleted, "Escape or dialog dismissal cannot discard unsaved text");
                typeof(TextFileEditorDialog).GetMethod("Discard_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
                await showing.WaitAsync(timeout.Token);
            }
            await VerifyRemoteTextEditorAsync(root, view, timeout.Token, Check, WaitFor, Dialog);
        }
        finally { File.Delete(path); }
        _window.Close();
        Program.Finish();
    }

    private async Task VerifyRemoteTextEditorAsync(Grid root, SftpWorkspaceView view, CancellationToken token,
        Action<bool, string> check, Func<Func<bool>, Task> waitFor, Func<TextFileEditorDialog> getDialog)
    {
        var remote = new SyntheticTextService();
        view.SetTextFileService(remote);
        var item = new RemoteFileItem { Name = "remote.txt", FullPath = "/untrusted/remote.txt", SizeBytes = 8 };
        view.Render(new(SftpSessionState.Idle, new("/", [item]), true, true, true, "", null));
        var remoteTable = (Syncfusion.UI.Xaml.DataGrid.SfDataGrid)view.FindName("RemoteTable");
        remoteTable.SelectedItem = item;
        typeof(SftpWorkspaceView).GetMethod("OpenSelectedRemoteText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        await Task.Delay(150, token);
        var editor = getDialog();
        await waitFor(() => !((ProgressRing)editor.FindName("LoadingRing")).IsActive);
        check(remote.ReadPath == "/remote.txt", "Remote entry point captures selected path");
        ((CheckBox)editor.FindName("ReadOnlyBox")).IsChecked = false;
        ((TextBox)editor.FindName("EditorText")).Text = "saved remotely";
        remote.SaveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = editor.SaveAsync();
        await waitFor(() => remote.SavePath is not null);
        check(!editor.TryClose() && ((TextBox)editor.FindName("EditorText")).IsReadOnly,
            "Saving blocks dismissal and further edits until completion");
        remote.SaveCompletion.SetResult();
        check(await saving && remote.SavePath == "/remote.txt" && Encoding.UTF8.GetString(remote.Content) == "saved remotely",
            "Remote save uses selected path and completes before closing");
        check(editor.TryClose(), "Clean remote editor closes");
        await Task.Delay(150, token);

        var delayed = new SyntheticTextService { ReadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var lateEditor = new TextFileEditorDialog(delayed, "/late.txt", "late.txt", true, root.XamlRoot, root.ActualTheme);
        var lateShowing = lateEditor.ShowAsync().AsTask();
        await waitFor(() => delayed.ReadPath is not null);
        check(lateEditor.TryClose(), "Loading editor can be cancelled");
        await lateShowing.WaitAsync(token);
        check(delayed.ReadToken.IsCancellationRequested, "Closing loading editor cancels the read");
        delayed.ReadCompletion.SetResult();
        await Task.Delay(100, token);
        check(((TextBox)lateEditor.FindName("EditorText")).Text == string.Empty, "Late read does not mutate a closed editor");

        var binary = new SyntheticTextService { Content = [0, 1, 2] };
        var failedEditor = new TextFileEditorDialog(binary, "/binary", "binary", true, root.XamlRoot, root.ActualTheme);
        var failedShowing = failedEditor.ShowAsync().AsTask();
        await waitFor(() => ((InfoBar)failedEditor.FindName("Notice")).IsOpen);
        check(!failedEditor.IsPrimaryButtonEnabled && ((InfoBar)failedEditor.FindName("Notice")).Severity == InfoBarSeverity.Error,
            "Binary file failure is visible and cannot be saved");
        failedEditor.TryClose();
        await failedShowing.WaitAsync(token);
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
