using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syncfusion.UI.Xaml.DataGrid;
using Syncfusion.UI.Xaml.Grids;
using System.Collections.ObjectModel;

namespace FluentShell.Views.Session;

public sealed partial class SftpWorkspaceView
{
    private readonly ObservableCollection<LocalPaneItem> _localFiles = [];
    private string? _localPath;
    private int _localReadVersion;
    private bool _localLoaded;
    public event EventHandler<IReadOnlyList<SftpUploadEntry>>? UploadSelectionRequested;
    public event EventHandler<SftpPaneDownload>? DownloadToLocalRequested;
    public event EventHandler<IReadOnlyList<RemoteFileItem>>? DownloadSelectionRequested;

    private void InitializeLocalPane()
    {
        LocalFiles.ItemsSource = _localFiles;
        LocalFiles.CanMaintainScrollPosition = false;
        LocalFiles.CellDoubleTapped += LocalFiles_DoubleTapped;
        LocalFiles.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(LocalFiles_KeyDown), true);
        LocalFiles.RecordContextFlyout = BuildLocalRowMenu();
        var emptyAreaMenu = new MenuFlyout();
        var refresh = new MenuFlyoutItem { Text = "刷新" };
        refresh.Click += (_, _) => RefreshLocalDirectory();
        emptyAreaMenu.Items.Add(refresh);
        ApplyChineseMenuFont(emptyAreaMenu);
        LocalFiles.RightTapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsOnRowOrChrome(source, LocalFiles)) return;
            e.Handled = true;
            emptyAreaMenu.ShowAt(LocalFiles, e.GetPosition(LocalFiles));
        };
        LocalFiles.GridContextFlyoutOpening += (_, e) =>
        {
            if (e.ContextFlyoutInfo is GridRecordContextFlyoutInfo { Record: LocalPaneItem item }
                && !LocalFiles.SelectedItems.Contains(item)) LocalFiles.SelectedItem = item;
        };
        LocalFiles.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "名称", MappingName = nameof(LocalPaneItem.SortName),
            CellTemplate = (DataTemplate)Resources["RemoteFileNameCellTemplate"],
            ColumnWidthMode = ColumnWidthMode.AutoLastColumnFill, MinimumWidth = 120
        });
        LocalFiles.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "类型", MappingName = nameof(LocalPaneItem.TypeLabel),
            CellTemplate = (DataTemplate)Resources["SftpTypeCellTemplate"], Width = 64
        });
        LocalFiles.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "大小", MappingName = nameof(LocalPaneItem.SizeBytes),
            CellTemplate = (DataTemplate)Resources["SftpSizeCellTemplate"], Width = 76
        });
        LocalFiles.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "修改时间", MappingName = nameof(LocalPaneItem.ModifiedAt),
            CellTemplate = (DataTemplate)Resources["SftpModifiedCellTemplate"], Width = 148,
            HeaderStyle = (Style)Resources["LastRemoteHeaderStyle"]
        });
        Loaded += (_, _) =>
        {
            _localLoaded = true;
            _ = NavigateLocalAsync(_localPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        };
        Unloaded += (_, _) => { _localLoaded = false; ++_localReadVersion; };
    }

    public void RefreshLocalDirectory()
    {
        if (_localLoaded && _localPath is not null) _ = NavigateLocalAsync(_localPath);
    }

    private async Task NavigateLocalAsync(string path)
    {
        var version = ++_localReadVersion;
        ToolTipService.SetToolTip(LocalPathBox, null);
        LocalFiles.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var fullPath = System.IO.Path.GetFullPath(path);
                var entries = new DirectoryInfo(fullPath).EnumerateFileSystemInfos()
                    .Where(info => _preferences.ShowHiddenFiles || (!info.Name.StartsWith('.') && (info.Attributes & FileAttributes.Hidden) == 0))
                    .Select(info => new LocalPaneItem(info.Name, info.FullName,
                        (info.Attributes & FileAttributes.Directory) != 0,
                        info is FileInfo file ? file.Length : 0, info.LastWriteTime))
                    .OrderByDescending(item => item.IsDirectory)
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                return (fullPath, entries);
            });
            if (version != _localReadVersion || !_localLoaded) return;
            _localPath = result.fullPath;
            LocalPathBox.Text = _localPath;
            LocalFiles.SelectedItem = null;
            _localFiles.Clear();
            if (LocalPaneItem.ParentOf(result.fullPath) is { } parent) _localFiles.Add(parent);
            foreach (var item in result.entries) _localFiles.Add(item);
        }
        catch (Exception exception)
        {
            if (version != _localReadVersion || !_localLoaded) return;
            LocalPathBox.Text = _localPath ?? string.Empty;
            await ShowFailureDialogAsync($"读取本地目录失败：{exception.Message}");
        }
        finally
        {
            if (version == _localReadVersion) LocalFiles.IsEnabled = true;
        }
    }

    private async void LocalPath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await NavigateLocalAsync(LocalPathBox.Text);
    }

    private Task NavigateLocalParentAsync() => _localPath is not null && Directory.GetParent(_localPath) is { } parent
        ? NavigateLocalAsync(parent.FullName) : Task.CompletedTask;

    private async void LocalFiles_DoubleTapped(object? sender, GridCellDoubleTappedEventArgs e)
    {
        await OpenLocalItemAsync();
    }

    private async void LocalFiles_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && LocalFiles.SelectedItem is LocalPaneItem)
        {
            e.Handled = true;
            await OpenLocalItemAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Back)
        {
            e.Handled = true;
            await NavigateLocalParentAsync();
        }
    }

    private MenuFlyout BuildLocalRowMenu()
    {
        var menu = new MenuFlyout();
        var refresh = new MenuFlyoutItem { Text = "刷新" };
        refresh.Click += (_, _) => RefreshLocalDirectory();
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
        var edit = new MenuFlyoutItem { Text = "查看/编辑文本" };
        edit.Click += async (_, _) => await OpenLocalItemAsync();
        menu.Items.Add(edit);
        var upload = new MenuFlyoutItem { Text = "上传" };
        menu.Opened += (_, _) =>
        {
            var selected = LocalFiles.SelectedItems.Cast<LocalPaneItem>().ToArray();
            edit.IsEnabled = selected.Length == 1 && !selected[0].IsDirectory;
            upload.IsEnabled = _snapshot.CanTransfer && selected.Length > 0 && selected.All(file => file.Name != "..");
        };
        upload.Click += (_, _) =>
        {
            var selected = LocalFiles.SelectedItems.Cast<LocalPaneItem>().ToArray();
            if (!_snapshot.CanTransfer || selected.Length == 0 || selected.Any(file => file.Name == "..")) return;
            var files = selected.Select(file => file.IsDirectory
                ? (SftpUploadEntry)new SftpUploadDirectory(file.Name, file.FullPath)
                : new SftpUploadFile(file.Name,
                    () => Task.Run<Stream>(() => new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)))).ToArray();
            UploadSelectionRequested?.Invoke(this, files);
        };
        menu.Items.Add(upload);
        ApplyChineseMenuFont(menu);
        return menu;
    }

    private void RequestDownloadToLocal()
    {
        var items = GetDownloadSelection();
        if (_localPath is not null && items.Count > 0 && _snapshot.CanTransfer)
            DownloadToLocalRequested?.Invoke(this, new SftpPaneDownload(items, _localPath));
    }

    public sealed record LocalPaneItem(string Name, string FullPath, bool IsDirectory, long SizeBytes, DateTime ModifiedAt)
    {
        public static LocalPaneItem? ParentOf(string path) => Directory.GetParent(path) is { } parent
            ? new LocalPaneItem("..", parent.FullName, true, 0, default) : null;

        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
        public string SortName => Name == ".." ? "0" : $"{(IsDirectory ? 1 : 2)}{Name}";
        public string ModifiedLabel => Name == ".." ? string.Empty : ModifiedAt.ToString("yyyy-MM-dd HH:mm");
        public string SizeLabel => IsDirectory ? "—" : SizeBytes switch
        {
            < 1024 => $"{SizeBytes} B",
            < 1048576 => $"{SizeBytes / 1024d:0.#} KB",
            < 1073741824 => $"{SizeBytes / 1048576d:0.#} MB",
            _ => $"{SizeBytes / 1073741824d:0.#} GB"
        };
        public string TypeLabel => Kind;
        public string Kind => IsDirectory ? "文件夹" : "文件";
    }
}
