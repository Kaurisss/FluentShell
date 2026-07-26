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

public sealed partial class SftpWorkspaceView : UserControl, ISftpWorkspaceView
{
    private readonly IntPtr _windowHandle;
    private readonly ObservableCollection<RemoteFileItem> _remoteFiles = [];
    private CancellationTokenSource? _transientStatusClear;
    private bool _isFailureDialogOpen;
    private SftpSessionSnapshot _snapshot = new(
        SftpSessionState.Idle,
        SftpDirectoryListing.Empty("/"),
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

    public void Render(SftpSessionSnapshot snapshot)
    {
        var previousState = _snapshot.State;
        var previousListing = _snapshot.DirectoryListing;
        _snapshot = snapshot;

        if (!ReferenceEquals(previousListing, snapshot.DirectoryListing))
            RenderDirectoryListing(snapshot.DirectoryListing);
        else if (snapshot.State == SftpSessionState.Failed)
            PathBox.Text = snapshot.DirectoryListing.Path;

        RenderWorkspaceOperationStatus(WorkspaceOperationStatusPresentation.From(snapshot));
        PathBox.IsEnabled = snapshot.CanNavigate;
        RemoteTable.IsEnabled = snapshot.CanNavigate;
        Toolbar.IsEnabled = snapshot.CanNavigate;
        UpdateSelectionState();

        if (snapshot.State == SftpSessionState.Failed && previousState != SftpSessionState.Failed)
            _ = ShowFailureDialogAsync(snapshot.ErrorMessage ?? snapshot.StatusMessage);
    }

    private void RenderDirectoryListing(SftpDirectoryListing listing)
    {
        RemoteTable.SelectedItem = null;
        RemoteTable.ItemsSource = null;
        _remoteFiles.Clear();
        foreach (var item in listing.Items) _remoteFiles.Add(item);
        RemoteTable.ItemsSource = _remoteFiles;
        PathBox.Text = listing.Path;
    }

    private void RenderWorkspaceOperationStatus(WorkspaceOperationStatusPresentation presentation)
    {
        if (presentation.ClearsAfterDelay)
            ScheduleTransientStatusClear();
        else
            CancelTransientStatusClear();

        WorkspaceOperationStatusPanel.Visibility = presentation.IsVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkspaceOperationProgress.IsActive = presentation.ShowsProgress;
        WorkspaceOperationProgress.Visibility = presentation.ShowsProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkspaceOperationStatus.Text = presentation.Message;
        ToolTipService.SetToolTip(WorkspaceOperationStatus, presentation.ToolTip ?? presentation.Message);
        CancelTransferButton.Visibility = presentation.CanCancelTransfer
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScheduleTransientStatusClear()
    {
        CancelTransientStatusClear();
        var cancellation = new CancellationTokenSource();
        _transientStatusClear = cancellation;
        _ = ClearTransientStatusAfterDelayAsync(cancellation);
    }

    private async Task ClearTransientStatusAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
            if (!ReferenceEquals(_transientStatusClear, cancellation)) return;

            _transientStatusClear = null;
            WorkspaceOperationStatusPanel.Visibility = Visibility.Collapsed;
            WorkspaceOperationStatus.Text = string.Empty;
            ToolTipService.SetToolTip(WorkspaceOperationStatus, null);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelTransientStatusClear()
    {
        var cancellation = _transientStatusClear;
        _transientStatusClear = null;
        cancellation?.Cancel();
    }

    private async Task ShowFailureDialogAsync(string message)
    {
        if (_isFailureDialogOpen || string.IsNullOrWhiteSpace(message) || XamlRoot is null) return;

        _isFailureDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "SFTP 操作失败",
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isFailureDialogOpen = false;
        }
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
        RemoteTable.CanMaintainScrollPosition = false;
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
        WorkspaceOperationStatus.MaxWidth = e.NewSize.Width < 760 ? 112 : 220;
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
        RenderWorkspaceOperationStatus(WorkspaceOperationStatusPresentation.Transient("已复制远程路径"));
    }

    private void UpdateSelectionState()
    {
        var item = SelectedItem;
        DownloadButton.IsEnabled = _snapshot.CanTransfer && item is { IsDirectory: false };
    }

    private sealed record WorkspaceOperationStatusPresentation(
        bool IsVisible,
        bool ShowsProgress,
        bool CanCancelTransfer,
        bool ClearsAfterDelay,
        string Message,
        string? ToolTip)
    {
        public static WorkspaceOperationStatusPresentation From(SftpSessionSnapshot snapshot) => snapshot.State switch
        {
            SftpSessionState.ListingDirectory => Active(snapshot.StatusMessage, canCancelTransfer: false),
            SftpSessionState.Transferring => Active(snapshot.StatusMessage, canCancelTransfer: true),
            SftpSessionState.Failed => Persistent(snapshot.ErrorMessage ?? snapshot.StatusMessage),
            SftpSessionState.Cancelled => Transient(snapshot.StatusMessage),
            _ when !string.IsNullOrWhiteSpace(snapshot.StatusMessage) => Transient(snapshot.StatusMessage),
            _ => Hidden
        };

        public static WorkspaceOperationStatusPresentation Persistent(string message) =>
            new(true, false, false, false, message, message);

        public static WorkspaceOperationStatusPresentation Transient(string message) =>
            new(true, false, false, true, message, message);

        private static WorkspaceOperationStatusPresentation Active(string message, bool canCancelTransfer) =>
            new(true, true, canCancelTransfer, false, message, message);

        private static WorkspaceOperationStatusPresentation Hidden =>
            new(false, false, false, false, string.Empty, null);
    }

    private static Microsoft.UI.Xaml.Data.Binding CreateOneWayBinding(string propertyName) => new()
    {
        Path = new Microsoft.UI.Xaml.PropertyPath(propertyName),
        Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
    };
}
