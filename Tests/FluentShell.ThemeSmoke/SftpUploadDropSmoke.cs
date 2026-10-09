using System.Reflection;
using FluentShell.Core;
using FluentShell.Views.Session;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifySftpUploadDropAsync()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FluentShell-drop-smoke-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var filePath = Path.Combine(fixture, "拖入文件.txt");
            await File.WriteAllTextAsync(filePath, "drag upload contents");
            var folderPath = Directory.CreateDirectory(Path.Combine(fixture, "拖入文件夹")).FullName;
            IStorageItem[] items = [await StorageFile.GetFileFromPathAsync(filePath), await StorageFolder.GetFolderFromPathAsync(folderPath)];
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(items);
            var text = new DataPackage();
            text.SetText("not a file");
            _window = new Window { Title = "FluentShell offline SFTP upload drop regression" };
            var root = new Grid();
            var view = new SftpWorkspaceView(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            root.Children.Add(view);
            _window.Content = root;
            _window.Activate();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            async Task WaitFor(Func<bool> condition)
            {
                while (!condition()) await Task.Delay(20, timeout.Token);
            }
            await WaitFor(() => view.IsLoaded);
            await ((Task)typeof(SftpWorkspaceView).GetMethod("NavigateLocalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [fixture])!).WaitAsync(timeout.Token);
            var receive = typeof(SftpWorkspaceView).GetMethod("ReceiveUploadDropAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var canAccept = typeof(SftpWorkspaceView).GetMethod("CanAcceptUploadDrop", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool Accepts(DataPackage data) => (bool)canAccept.Invoke(view, [data.GetView()])!;
            Task Drop(DataPackage data, Action<DataPackageOperation> complete) =>
                ((Task)receive.Invoke(view, [data.GetView(), complete])!).WaitAsync(timeout.Token);
            void Render(string path = "/上传目标", bool navigate = true, bool transfer = true) =>
                view.Render(new SftpSessionSnapshot(navigate ? SftpSessionState.Idle : SftpSessionState.ListingDirectory,
                    SftpDirectoryListing.Empty(path), navigate, navigate, transfer, "", null));
            void Check(bool passed, string message)
            {
                if (!passed) throw new InvalidOperationException(message);
                Program.Results.Add(message);
            }
            var calls = 0;
            string? target = null;
            var completed = false;
            Func<IReadOnlyList<SftpUploadEntry>, string, Task> handler = async (entries, path) =>
            {
                Check(completed, "Drop completes before upload starts");
                Check(entries.Count == 2 && entries[0] is SftpUploadFile && entries[1] is SftpUploadDirectory,
                    "StorageItems preserves mixed file and folder selection");
                using var input = await ((SftpUploadFile)entries[0]).OpenRead();
                using var reader = new StreamReader(input);
                Check(await reader.ReadToEndAsync() == "drag upload contents", "Dropped file remains readable after deferral completion");
                Check(((SftpUploadDirectory)entries[1]).LocalPath == folderPath, "Dropped folder retains its local root");
                calls++;
                target = path;
            };
            view.SetUploadDropHandler(handler);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(theme == ElementTheme.Light
                    ? Microsoft.UI.Colors.WhiteSmoke : Microsoft.UI.Colors.Black);
                _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(theme == ElementTheme.Light ? 1100 : 760, 700));
                Render();
                root.UpdateLayout();
                await Task.Delay(200, timeout.Token);
                var pane = (Grid)view.FindName("RemotePane");
                Check(pane.AllowDrop && pane.Background is not null && pane.ActualWidth > 0, $"{theme}: remote pane is a visible hit-testable drop target");
                Check(Accepts(package) && !Accepts(text), $"{theme}: accepts StorageItems and rejects text");
                completed = false;
                await Drop(package, operation => { Check(operation == DataPackageOperation.Copy, $"{theme}: upload uses Copy"); completed = true; });
                Check(target == "/上传目标", $"{theme}: upload captures current remote directory");
                await CaptureAsync(root, Program.ReportPath + $".{theme}.png");
            }
            foreach (var state in new[] { (navigate: false, transfer: true), (navigate: true, transfer: false) })
            {
                Render(navigate: state.navigate, transfer: state.transfer);
                Check(!Accepts(package), "Busy or disconnected snapshot rejects drag");
                await Drop(package, operation => Check(operation == DataPackageOperation.None, "Rejected drop performs no copy"));
            }
            Render();
            await Drop(text, operation => Check(operation == DataPackageOperation.None, "Text drop performs no copy"));
            Check(calls == 2, "Rejected drops do not start uploads");

            var local = (Syncfusion.UI.Xaml.DataGrid.SfDataGrid)view.FindName("LocalFiles");
            var localItems = ((System.Collections.IEnumerable)local.ItemsSource).Cast<SftpWorkspaceView.LocalPaneItem>().ToArray();
            var localFile = localItems.Single(item => item.FullPath == filePath);
            var localFolder = localItems.Single(item => item.FullPath == folderPath);
            local.SelectedItems.Add(localFile);
            local.SelectedItems.Add(localFolder);
            var prepare = typeof(SftpWorkspaceView).GetMethod("PrepareLocalDragAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task<bool> Prepare(SftpWorkspaceView.LocalPaneItem item, DataPackage data) =>
                ((Task<bool>)prepare.Invoke(view, [item, data])!).WaitAsync(timeout.Token);
            var localPackage = new DataPackage();
            Check(await Prepare(localFile, localPackage), "Local pane prepares selected file and folder for native dragging");
            Check(localPackage.RequestedOperation == DataPackageOperation.Copy &&
                (await localPackage.GetView().GetStorageItemsAsync()).Count == 2, "Local pane drag preserves multi-selection and Copy semantics");
            Render();
            completed = false;
            await Drop(localPackage, operation => { Check(operation == DataPackageOperation.Copy, "Local pane drop accepted"); completed = true; });
            Check(!await Prepare(localItems.Single(item => item.Name == ".."), new DataPackage()), "Parent navigation row cannot be uploaded");
            local.UpdateLayout();
            Check(Descendants(local).OfType<Grid>().Any(grid => grid.CanDrag && grid.DataContext is SftpWorkspaceView.LocalPaneItem),
                "Local filename cells expose native drag sources");

            // Simulate Explorer supplying StorageItems later, while navigation or tab lifetime changes.
            async Task LateDrop(Action duringRead, bool expectedCopy)
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var delayed = new DataPackage();
                delayed.SetDataProvider(StandardDataFormats.StorageItems, async request =>
                {
                    var deferral = request.GetDeferral();
                    try
                    {
                        started.TrySetResult();
                        await release.Task;
                        request.SetData(items);
                    }
                    finally { deferral.Complete(); }
                });
                completed = false;
                var drop = Drop(delayed, operation =>
                {
                    Check(operation == (expectedCopy ? DataPackageOperation.Copy : DataPackageOperation.None), "Late data honors connection and view lifetime");
                    completed = true;
                });
                try
                {
                    await started.Task.WaitAsync(timeout.Token);
                    Check(!Accepts(package), "Data retrieval rejects overlapping drops");
                    await Drop(package, operation => Check(operation == DataPackageOperation.None, "Overlapping drop rejected"));
                    Check(!Accepts(package), "Overlapping drop does not unlock first request");
                    duringRead();
                    if (view.Parent is not null) await WaitFor(() => view.IsLoaded);
                }
                finally { release.TrySetResult(); }
                await drop;
            }
            Render();
            await LateDrop(() => Render("/另一个目录"), expectedCopy: true);
            Check(target == "/上传目标" && calls == 4, "Navigation during data retrieval cannot redirect upload");
            Render();
            await LateDrop(() => Render(transfer: false), expectedCopy: false);
            Render();
            await LateDrop(() => view.SetUploadDropHandler(null), expectedCopy: false);
            view.SetUploadDropHandler(handler);
            Render();
            await LateDrop(() =>
            {
                root.Children.Clear();
                root.UpdateLayout();
                root.Children.Add(view);
                root.UpdateLayout();
            }, expectedCopy: false);
            Render();
            await LateDrop(() => { root.Children.Clear(); root.UpdateLayout(); }, expectedCopy: false);
            Check(calls == 4, "Disconnect, disposal and unloading discard late drops");
            _window.Close();
        }
        finally { Directory.Delete(fixture, recursive: true); }
        Program.Finish();
    }
}
