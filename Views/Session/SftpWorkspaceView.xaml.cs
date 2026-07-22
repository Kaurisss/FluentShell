using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syncfusion.UI.Xaml.DataGrid;
using Syncfusion.UI.Xaml.Grids;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentShell.Views.Session;

public sealed record SftpUploadFile(string Name, Func<Task<Stream>> OpenRead);

public sealed partial class SftpWorkspaceView : UserControl
{
    private readonly IntPtr _windowHandle;
    private readonly ObservableCollection<RemoteFileItem> _remoteFiles = [];
    private SftpSessionSnapshot _snapshot = new(
        SftpSessionState.Idle,
        "/",
        false,
        false,
        false,
        false,
        string.Empty,
        null);

    public SftpWorkspaceView(IntPtr windowHandle, ElementTheme workspaceTheme)
    {
        _windowHandle = windowHandle;
        RequestedTheme = workspaceTheme;
        InitializeComponent();
        ConfigureRemoteTable();
        SizeChanged += SftpWorkspaceView_SizeChanged;
    }

    public RemoteFileItem? SelectedItem => RemoteTable.SelectedItem as RemoteFileItem;

    public event EventHandler? RefreshRequested;
    public event EventHandler<string>? NavigateRequested;
    public event EventHandler? NewFolderRequested;
    public event EventHandler? UploadRequested;
    public event EventHandler<RemoteFileItem>? DownloadRequested;
    public event EventHandler<RemoteFileItem>? RenameRequested;
    public event EventHandler<RemoteFileItem>? DeleteRequested;
    public event EventHandler? CancelTransferRequested;

    public void RenderDirectory(SftpDirectoryListing listing)
    {
        if (!listing.Succeeded)
        {
            TransferStatus.Text = listing.ErrorMessage ?? "读取目录失败。";
            return;
        }

        RemoteTable.SelectedItem = null;
        _remoteFiles.Clear();
        foreach (var item in listing.Items) _remoteFiles.Add(item);
        PathBox.Text = listing.Path;
        UpdateSelectionState();
    }

    public void RenderState(SftpSessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        var isListing = snapshot.State == SftpSessionState.ListingDirectory;
        DirectoryProgress.IsActive = isListing;
        DirectoryProgress.Visibility = isListing ? Visibility.Visible : Visibility.Collapsed;
        DirectoryStatus.Text = isListing ? snapshot.StatusMessage : snapshot.ErrorMessage ?? string.Empty;
        ToolTipService.SetToolTip(DirectoryStatus, snapshot.ErrorMessage);
        PathBox.IsEnabled = snapshot.CanNavigate;
        RemoteTable.IsEnabled = snapshot.CanNavigate;
        Toolbar.IsEnabled = snapshot.CanNavigate;
        TransferProgress.Visibility = snapshot.State == SftpSessionState.Transferring
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelTransferButton.Visibility = snapshot.State == SftpSessionState.Transferring
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage) && !isListing)
            TransferStatus.Text = snapshot.StatusMessage;
        UpdateSelectionState();
    }

    public void ShowOperationResult(SftpOperationResult result)
    {
        TransferStatus.Text = result.Message;
        if (!result.Succeeded)
            ToolTipService.SetToolTip(TransferStatus, result.Message);
    }

    public async Task<string> PromptTextAsync(string title, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : string.Empty;
    }

    public async Task<bool> ConfirmOverwriteAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = "文件已存在",
            Content = $"“{name}”已存在，是否覆盖？",
            PrimaryButtonText = "覆盖",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmDeleteAsync(RemoteFileItem item)
    {
        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = $"确定删除“{item.Name}”吗？仅允许删除空目录。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<IReadOnlyList<SftpUploadFile>> PickUploadFilesAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(file => new SftpUploadFile(file.Name, file.OpenStreamForReadAsync)).ToList();
    }

    public async Task<string?> PickDownloadDirectoryAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void ConfigureRemoteTable()
    {
        RemoteTable.ItemsSource = _remoteFiles;
        RemoteTable.CellDoubleTapped += RemoteTable_CellDoubleTapped;
        RemoteTable.SelectionChanged += RemoteTable_SelectionChanged;
        RemoteTable.GridContextFlyoutOpening += RemoteTable_GridContextFlyoutOpening;
        RemoteTable.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RemoteTable_KeyDown), true);
        RemoteTable.RecordContextFlyout = BuildRemoteRowMenu();

        RemoteTable.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "名称",
            MappingName = nameof(RemoteFileItem.SortName),
            CellTemplate = (DataTemplate)Resources["RemoteFileNameCellTemplate"],
            ColumnWidthMode = ColumnWidthMode.AutoLastColumnFill,
            MinimumWidth = 180
        });
        RemoteTable.Columns.Add(new GridTextColumn
        {
            HeaderText = "类型",
            MappingName = nameof(RemoteFileItem.TypeLabel),
            MinimumWidth = 80,
            MaximumWidth = 160
        });
        RemoteTable.Columns.Add(new GridTextColumn
        {
            HeaderText = "大小",
            MappingName = nameof(RemoteFileItem.SizeBytes),
            DisplayBinding = CreateOneWayBinding(nameof(RemoteFileItem.SizeLabel)),
            MinimumWidth = 96,
            MaximumWidth = 160,
            TextAlignment = TextAlignment.Right
        });
        RemoteTable.Columns.Add(new GridTextColumn
        {
            HeaderText = "修改时间",
            MappingName = nameof(RemoteFileItem.ModifiedAt),
            DisplayBinding = CreateOneWayBinding(nameof(RemoteFileItem.ModifiedLabel)),
            MinimumWidth = 150,
            MaximumWidth = 230
        });
    }

    private MenuFlyout BuildRemoteRowMenu()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开文件夹" };
        open.Click += (_, _) => OpenSelectedDirectory();
        var download = new MenuFlyoutItem { Text = "下载" };
        download.Click += (_, _) => RequestDownload();
        var copyPath = new MenuFlyoutItem { Text = "复制远程路径" };
        copyPath.Click += (_, _) => CopySelectedRemotePath();
        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += (_, _) => RequestRename();
        var delete = new MenuFlyoutItem { Text = "删除" };
        delete.Click += (_, _) => RequestDelete();
        menu.Items.Add(open);
        menu.Items.Add(download);
        menu.Items.Add(copyPath);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Opened += (_, _) =>
        {
            var item = SelectedItem;
            open.IsEnabled = _snapshot.CanNavigate && item?.IsDirectory == true;
            download.IsEnabled = _snapshot.CanTransfer && item is { IsDirectory: false };
            copyPath.IsEnabled = item is not null;
            rename.IsEnabled = _snapshot.CanModifyRemoteFiles && item is not null && item.Name != "..";
            delete.IsEnabled = _snapshot.CanModifyRemoteFiles && item is not null && item.Name != "..";
        };
        return menu;
    }

    private void SftpWorkspaceView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 560;
        DirectoryStatus.MaxWidth = e.NewSize.Width < 760 ? 112 : 220;
        DirectoryStatusPanel.Visibility = !isNarrow || _snapshot.State == SftpSessionState.ListingDirectory
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void NewFolderButton_Click(object sender, RoutedEventArgs e) =>
        NewFolderRequested?.Invoke(this, EventArgs.Empty);

    private void UploadButton_Click(object sender, RoutedEventArgs e) =>
        UploadRequested?.Invoke(this, EventArgs.Empty);

    private void DownloadButton_Click(object sender, RoutedEventArgs e) => RequestDownload();

    private void CancelTransferButton_Click(object sender, RoutedEventArgs e) =>
        CancelTransferRequested?.Invoke(this, EventArgs.Empty);

    private void RemoteTable_CellDoubleTapped(object? sender, GridCellDoubleTappedEventArgs e) =>
        OpenSelectedDirectory();

    private void RemoteTable_SelectionChanged(object? sender, GridSelectionChangedEventArgs e) =>
        UpdateSelectionState();

    private void RemoteTable_GridContextFlyoutOpening(object? sender, GridContextFlyoutEventArgs e)
    {
        if (e.ContextFlyoutInfo is GridRecordContextFlyoutInfo { Record: RemoteFileItem item })
            RemoteTable.SelectedItem = item;
    }

    private void RemoteTable_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            OpenSelectedDirectory();
        }
        else if (e.Key == Windows.System.VirtualKey.F2)
        {
            e.Handled = true;
            RequestRename();
        }
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            RequestDelete();
        }
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        NavigateRequested?.Invoke(this, PathBox.Text);
    }

    private void OpenSelectedDirectory()
    {
        if (SelectedItem is { IsDirectory: true } item && _snapshot.CanNavigate)
            NavigateRequested?.Invoke(this, item.FullPath);
    }

    private void RequestDownload()
    {
        if (SelectedItem is { IsDirectory: false } item && _snapshot.CanTransfer)
            DownloadRequested?.Invoke(this, item);
    }

    private void RequestRename()
    {
        if (SelectedItem is { Name: not ".." } item && _snapshot.CanModifyRemoteFiles)
            RenameRequested?.Invoke(this, item);
    }

    private void RequestDelete()
    {
        if (SelectedItem is { Name: not ".." } item && _snapshot.CanModifyRemoteFiles)
            DeleteRequested?.Invoke(this, item);
    }

    private void CopySelectedRemotePath()
    {
        if (SelectedItem is not { } item) return;
        var package = new DataPackage();
        package.SetText(item.FullPath);
        Clipboard.SetContent(package);
        TransferStatus.Text = "已复制远程路径";
    }

    private void UpdateSelectionState()
    {
        var item = SelectedItem;
        DownloadButton.IsEnabled = _snapshot.CanTransfer && item is { IsDirectory: false };
    }

    private static Microsoft.UI.Xaml.Data.Binding CreateOneWayBinding(string propertyName) => new()
    {
        Path = new Microsoft.UI.Xaml.PropertyPath(propertyName),
        Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
    };
}