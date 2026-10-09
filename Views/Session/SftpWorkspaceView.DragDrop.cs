using FluentShell.Core;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FluentShell.Views.Session;

public sealed partial class SftpWorkspaceView
{
    private Func<IReadOnlyList<SftpUploadEntry>, string, Task>? _uploadDropHandler;
    private bool _readingDrop;
    private int _dropVersion;

    public void SetUploadDropHandler(Func<IReadOnlyList<SftpUploadEntry>, string, Task>? handler) =>
        _uploadDropHandler = handler;

    private void InitializeUploadDropTarget()
    {
        // Observe routed events even if a grid cell or the path TextBox handles them.
        RemotePane.AddHandler(UIElement.DragOverEvent, new DragEventHandler(RemotePane_DragOver), true);
        RemotePane.AddHandler(UIElement.DropEvent, new DragEventHandler(RemotePane_Drop), true);
        Unloaded += (_, _) => ++_dropVersion;
    }

    private bool CanAcceptUploadDrop(DataPackageView data) =>
        IsLoaded && !_readingDrop && _uploadDropHandler is not null &&
        _snapshot.CanNavigate && _snapshot.CanTransfer && data.Contains(StandardDataFormats.StorageItems);

    private async void LocalItem_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        string? error = null;
        try
        {
            e.Cancel = sender is not FrameworkElement { DataContext: LocalPaneItem item } ||
                !await PrepareLocalDragAsync(item, e.Data);
        }
        catch (Exception exception)
        {
            e.Cancel = true;
            error = exception.Message;
        }
        finally { deferral.Complete(); }
        if (error is not null) await ShowFailureDialogAsync($"无法拖动本地文件：{error}", "拖放上传失败");
    }

    private async Task<bool> PrepareLocalDragAsync(LocalPaneItem item, DataPackage data)
    {
        if (!_localLoaded || _localOperationBusy || !LocalFiles.IsEnabled || !_snapshot.CanTransfer || item.Name == "..") return false;
        var selected = LocalFiles.SelectedItems.Cast<LocalPaneItem>().ToArray();
        if (!selected.Contains(item))
        {
            LocalFiles.SelectedItem = item;
            selected = [item];
        }
        if (selected.Any(entry => entry.Name == "..")) return false;
        var version = _dropVersion;
        var items = new List<IStorageItem>();
        foreach (var entry in selected)
        {
            items.Add(entry.IsDirectory
                ? await StorageFolder.GetFolderFromPathAsync(entry.FullPath)
                : await StorageFile.GetFileFromPathAsync(entry.FullPath));
        }
        if (!_localLoaded || version != _dropVersion || !_snapshot.CanTransfer) return false;
        data.SetStorageItems(items);
        data.RequestedOperation = DataPackageOperation.Copy;
        return true;
    }

    private void RemotePane_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = CanAcceptUploadDrop(e.DataView) ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (e.AcceptedOperation == DataPackageOperation.Copy)
        {
            e.DragUIOverride.Caption = $"上传到 {_snapshot.DirectoryListing.Path}";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    private async void RemotePane_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            await ReceiveUploadDropAsync(e.DataView, operation =>
            {
                e.AcceptedOperation = operation;
                deferral.Complete();
            });
        }
        catch (Exception exception)
        {
            await ShowFailureDialogAsync($"无法上传拖入的文件或文件夹：{exception.Message}", "拖放上传失败");
        }
    }

    private async Task ReceiveUploadDropAsync(DataPackageView data, Action<DataPackageOperation> complete)
    {
        var operation = DataPackageOperation.None;
        var ownsRead = false;
        var entries = new List<SftpUploadEntry>();
        var handler = _uploadDropHandler;
        var version = _dropVersion;
        // Capture before requesting data from the source, which may respond asynchronously.
        var targetPath = _snapshot.DirectoryListing.Path;
        try
        {
            if (!CanAcceptUploadDrop(data)) return;
            _readingDrop = ownsRead = true;
            foreach (var item in await data.GetStorageItemsAsync())
            {
                entries.Add(item switch
                {
                    StorageFile file => new SftpUploadFile(file.Name, file.OpenStreamForReadAsync),
                    StorageFolder folder when !string.IsNullOrWhiteSpace(folder.Path) =>
                        new SftpUploadDirectory(folder.Name, folder.Path),
                    _ => throw new IOException("仅支持可读取的文件和本地文件夹。")
                });
            }

            // A tab can close or disconnect while the drag source supplies its data.
            if (entries.Count == 0 || !IsLoaded || version != _dropVersion || !ReferenceEquals(handler, _uploadDropHandler) ||
                !_snapshot.CanTransfer) return;
            operation = DataPackageOperation.Copy;
        }
        finally
        {
            if (ownsRead) _readingDrop = false;
            // Release Explorer before a long transfer or an overwrite dialog begins.
            complete(operation);
        }

        await handler!(entries, targetPath);
    }
}
