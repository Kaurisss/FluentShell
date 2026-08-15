using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private MenuFlyout? _emptyAreaMenu;
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
        var previousTransferState = _snapshot.Transfer.State;
        var previousListing = _snapshot.DirectoryListing;
        _snapshot = snapshot;

        if (!ReferenceEquals(previousListing, snapshot.DirectoryListing))
            RenderDirectoryListing(snapshot.DirectoryListing);
        else if (snapshot.State == SftpSessionState.Failed)
            PathBox.Text = snapshot.DirectoryListing.Path;

        RenderTransferTip(snapshot);
        RenderWorkspaceOperationStatus(WorkspaceOperationStatusPresentation.From(snapshot));
        PathBox.IsEnabled = snapshot.CanNavigate;
        RemoteTable.IsEnabled = snapshot.CanNavigate;
        Toolbar.IsEnabled = snapshot.CanNavigate;
        UploadButton.IsEnabled = snapshot.CanTransfer;
        UpdateSelectionState();

        // 目录读取失败是浏览途中的常态（没权限、路径敲错），内联状态足够；
        // 只有明确下达的文件操作失败才值得用弹窗打断。
        if (snapshot.State == SftpSessionState.Failed &&
            previousState != SftpSessionState.Failed &&
            snapshot.FailureKind == SftpFailureKind.Operation)
        {
            _ = ShowFailureDialogAsync(snapshot.ErrorMessage ?? snapshot.StatusMessage);
        }

        // 传输失败在自己的轴上弹窗解释（部分完成的明细都在消息里）。
        if (snapshot.Transfer.State == SftpTransferState.Failed &&
            previousTransferState != SftpTransferState.Failed)
        {
            _ = ShowFailureDialogAsync(snapshot.Transfer.Message);
        }
    }

    public void ShowTransferStatus()
    {
        if (!TransferTip.IsOpen)
        {
            // 打开时第一份传输快照可能还没到，别让面板展示上一批的旧内容。
            if (!_snapshot.Transfer.IsActive)
            {
                TransferTipMessage.Text = "正在准备传输…";
                TransferTipBar.IsIndeterminate = true;
                TransferTipBytes.Text = string.Empty;
            }
            TransferTip.IsOpen = true;
        }
        UpdateTransferStatusButton();
    }

    private void TransferStatusButton_Click(object sender, RoutedEventArgs e)
    {
        TransferTip.IsOpen = !TransferTip.IsOpen;
        UpdateTransferStatusButton();
    }

    private void TransferTip_ActionButtonClick(TeachingTip sender, object args)
    {
        CancelTransferRequested?.Invoke(this, EventArgs.Empty);
        // 用户点击取消后立即关闭面板，避免转圈等待造成的混淆
        TransferTip.IsOpen = false;
    }

    private void TransferTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
        UpdateTransferStatusButton();

    private void RenderTransferTip(SftpSessionSnapshot snapshot)
    {
        var transfer = snapshot.Transfer;
        // 取消与失败由内联提示和失败弹窗接手；完成保持面板打开显示汇总。
        if (transfer.State is SftpTransferState.Failed or SftpTransferState.Cancelled)
        {
            TransferTip.IsOpen = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(transfer.Message))
            TransferTipMessage.Text = transfer.Message;
        // 只有传输中可取消；其余阶段收起动作按钮。
        TransferTip.ActionButtonContent = transfer.IsActive ? "取消传输" : null;

        var progress = transfer.Progress;
        TransferTipBar.Visibility = transfer.IsActive ? Visibility.Visible : Visibility.Collapsed;
        TransferTipBar.IsIndeterminate = progress is null;
        if (progress is not null) TransferTipBar.Value = progress.Percent;

        // 更新字节数、速度和剩余时间显示
        if (progress is null)
        {
            TransferTipBytes.Text = string.Empty;
        }
        else
        {
            var parts = new List<string>
            {
                $"{FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)}"
            };

            if (progress.BytesPerSecond > 0)
                parts.Add(FormatBytesPerSecond(progress.BytesPerSecond));

            if (progress.EstimatedSecondsRemaining is not null)
                parts.Add($"剩余 {FormatTimeRemaining(progress.EstimatedSecondsRemaining)}");

            TransferTipBytes.Text = string.Join("  •  ", parts);
        }

        // 更新传输队列列表
        TransferQueueList.ItemsSource = snapshot.Queue.Items;
        TransferQueueList.Visibility = snapshot.Queue.HasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 传输状态按钮既是传输中的活动指示，也是面板开着时的锚点——
    /// 面板未关就不能藏按钮，否则 TeachingTip 失去目标会飘。
    /// </summary>
    private void UpdateTransferStatusButton()
    {
        var transfer = _snapshot.Transfer;
        // 取消或失败后应立即隐藏按钮，即使面板还在关闭动画中
        var shouldShow = transfer.State switch
        {
            SftpTransferState.Cancelled => false,
            SftpTransferState.Failed => false,
            _ => transfer.IsActive || TransferTip.IsOpen
        };
        TransferStatusButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        TransferStatusButtonRing.IsActive = transfer.IsActive && transfer.State == SftpTransferState.Transferring;
        TransferStatusButtonRing.Visibility = TransferStatusButtonRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        TransferStatusButtonLabel.Text = transfer.State switch
        {
            SftpTransferState.Transferring =>
                transfer.Progress?.Percent is double percent ? $"{(int)percent}%" : "…",
            SftpTransferState.Completed => "完成",
            _ => "…"
        };
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:0.0} GB"
    };

    private static string FormatBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond < 0) return "—";

        return bytesPerSecond switch
        {
            < 1024 => $"{bytesPerSecond:0.0} B/s",
            < 1024 * 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
            < 1024L * 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:0.0} MB/s",
            _ => $"{bytesPerSecond / 1024 / 1024 / 1024:0.0} GB/s"
        };
    }

    private static string FormatTimeRemaining(double? seconds)
    {
        if (seconds is not double sec || sec < 0 || double.IsInfinity(sec) || double.IsNaN(sec))
            return "—";

        var totalSeconds = (int)Math.Ceiling(sec);

        if (totalSeconds < 60)
            return $"{totalSeconds} 秒";

        if (totalSeconds < 3600)
        {
            var minutes = totalSeconds / 60;
            var remainingSeconds = totalSeconds % 60;
            return remainingSeconds > 0
                ? $"{minutes} 分 {remainingSeconds} 秒"
                : $"{minutes} 分";
        }

        var hours = totalSeconds / 3600;
        var remainingMinutes = (totalSeconds % 3600) / 60;
        return remainingMinutes > 0
            ? $"{hours} 小时 {remainingMinutes} 分"
            : $"{hours} 小时";
    }

    private void RenderDirectoryListing(SftpDirectoryListing listing)
    {
        // 就地增删，不重新赋值 ItemsSource：整体重绑会让表格重算列宽，而填充剩余宽度的
        // 那一列填不满，表头右侧会留下大片空白。
        RemoteTable.SelectedItem = null;
        _remoteFiles.Clear();
        foreach (var item in listing.Items) _remoteFiles.Add(item);
        PathBox.Text = listing.Path;
    }

    private void RenderWorkspaceOperationStatus(WorkspaceOperationStatusPresentation presentation)
    {
        if (presentation.ClearsAfterDelay)
            ScheduleTransientStatusClear();
        else
            CancelTransientStatusClear();

        WorkspaceOperationStatusPanel.Visibility =
            presentation.ShowsInlineMessage || presentation.ShowsListingIndicator || presentation.IsTransferring
                ? Visibility.Visible
                : Visibility.Collapsed;
        WorkspaceOperationProgress.IsActive = presentation.ShowsListingIndicator;
        WorkspaceOperationProgress.Visibility = presentation.ShowsListingIndicator
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 传输中内联只留一个带进度的小按钮，详情都在传输面板里。
        UpdateTransferStatusButton();
        WorkspaceOperationStatus.Visibility = presentation.ShowsInlineMessage
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkspaceOperationStatus.Text = presentation.Message;
        ToolTipService.SetToolTip(WorkspaceOperationStatus, presentation.ToolTip ?? presentation.Message);
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
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isFailureDialogOpen = false;
        }
    }

    public async Task<string> PromptTextAsync(string title, string placeholder, string initialText = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initialText };
        // 预填全选：重命名时直接输入即整体替换，想改一部分再点进去。
        box.SelectionStart = 0;
        box.SelectionLength = initialText.Length;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : string.Empty;
    }

    public async Task<bool> ConfirmOverwriteAsync(string name)
    {
        // 传输面板是非模态的 TeachingTip，与 ContentDialog 可以共存，无须收起。
        var dialog = new ContentDialog
        {
            Title = "文件已存在",
            Content = $"“{name}”已存在，是否覆盖？",
            PrimaryButtonText = "覆盖",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmDeleteAsync(RemoteFileItem item)
    {
        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = item.IsDirectory
                ? $"确定删除文件夹“{item.Name}”吗？仅允许删除空文件夹。"
                : $"确定删除文件“{item.Name}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
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
        // 行上的右键由 RecordContextFlyout 接管；这里兜住落在空白区的右键，
        // 否则空目录（连 ".." 行都没有时）就没有新建文件夹的入口了。
        _emptyAreaMenu = BuildEmptyAreaMenu();
        RemoteTable.RightTapped += RemoteTable_RightTapped;

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
        var newFolder = new MenuFlyoutItem { Text = "新建文件夹" };
        newFolder.Click += (_, _) => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        var properties = new MenuFlyoutItem { Text = "属性" };
        properties.Click += (_, _) => _ = ShowSelectedItemPropertiesAsync();
        menu.Items.Add(open);
        menu.Items.Add(download);
        menu.Items.Add(copyPath);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(newFolder);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(properties);
        ApplyChineseMenuFont(menu);
        menu.Opened += (_, _) =>
        {
            var item = SelectedItem;
            open.IsEnabled = _snapshot.CanNavigate && item?.IsDirectory == true;
            download.IsEnabled = _snapshot.CanTransfer && item is { Name: not ".." };
            copyPath.IsEnabled = item is not null;
            rename.IsEnabled = _snapshot.CanModifyRemoteFiles && item is not null && item.Name != "..";
            delete.IsEnabled = _snapshot.CanModifyRemoteFiles && item is not null && item.Name != "..";
            // 新建文件夹作用于当前目录，与选中项无关。
            newFolder.IsEnabled = _snapshot.CanModifyRemoteFiles;
            // ".." 是本地合成的父目录条目，大小、修改时间都是占位值，没有属性可看。
            properties.IsEnabled = item is { Name: not ".." };
        };
        return menu;
    }

    private MenuFlyout BuildEmptyAreaMenu()
    {
        var menu = new MenuFlyout();
        var refresh = new MenuFlyoutItem { Text = "刷新" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var upload = new MenuFlyoutItem { Text = "上传" };
        upload.Click += (_, _) => UploadRequested?.Invoke(this, EventArgs.Empty);
        var newFolder = new MenuFlyoutItem { Text = "新建文件夹" };
        newFolder.Click += (_, _) => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(upload);
        menu.Items.Add(newFolder);
        ApplyChineseMenuFont(menu);
        menu.Opened += (_, _) =>
        {
            refresh.IsEnabled = _snapshot.CanNavigate;
            upload.IsEnabled = _snapshot.CanTransfer;
            newFolder.IsEnabled = _snapshot.CanModifyRemoteFiles;
        };
        return menu;
    }

    /// <summary>
    /// Syncfusion 的表格样式把字体设成了系统上并不存在的 "Segoe UI Variable Static Text"，
    /// 右键菜单挂在表格下会继承它；而 Segoe UI Variable 系列同样不含中文字形，菜单文字只能走
    /// 字体回退，在本机落到宋体系的衬线字体上。菜单项全是中文，这里直接指定含中文字形的黑体。
    /// </summary>
    private static void ApplyChineseMenuFont(MenuFlyout menu)
    {
        var menuFontFamily = new FontFamily("Microsoft YaHei UI");
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            item.FontFamily = menuFontFamily;
    }

    private void RemoteTable_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 落在单元格、表头或滚动条上的右键不归这里管：单元格有行菜单，表头、滚动条不该弹目录菜单。
        if (e.OriginalSource is DependencyObject source && IsOnRowOrChrome(source)) return;

        e.Handled = true;
        _emptyAreaMenu?.ShowAt(RemoteTable, e.GetPosition(RemoteTable));
    }

    private bool IsOnRowOrChrome(DependencyObject source)
    {
        for (var current = source;
             current is not null && !ReferenceEquals(current, RemoteTable);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is GridCell
                or GridHeaderCellControl
                or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar)
            {
                return true;
            }
        }
        return false;
    }

    private void SftpWorkspaceView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WorkspaceOperationStatus.MaxWidth = e.NewSize.Width < 760 ? 112 : 220;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void UploadButton_Click(object sender, RoutedEventArgs e) =>
        UploadRequested?.Invoke(this, EventArgs.Empty);

    private void DownloadButton_Click(object sender, RoutedEventArgs e) => RequestDownload();

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
        else if (e.Key == Windows.System.VirtualKey.Back && _snapshot.CanNavigate)
        {
            // 与资源管理器一致：Backspace 返回上级目录。".." 由 RemotePath.Normalize 解析。
            e.Handled = true;
            NavigateRequested?.Invoke(this, "..");
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
        if (SelectedItem is { Name: not ".." } item && _snapshot.CanTransfer)
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

    private async Task ShowSelectedItemPropertiesAsync()
    {
        if (SelectedItem is not { Name: not ".." } item || XamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title = $"“{item.Name}”属性",
            Content = BuildPropertiesPanel(item),
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static Grid BuildPropertiesPanel(RemoteFileItem item)
    {
        var panel = new Grid { ColumnSpacing = 16, RowSpacing = 8, MinWidth = 360 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void AddRow(string label, string value)
        {
            var row = panel.RowDefinitions.Count;
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var labelBlock = new TextBlock { Text = label, Opacity = 0.7 };
            Grid.SetRow(labelBlock, row);
            var valueBlock = new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(valueBlock, row);
            Grid.SetColumn(valueBlock, 1);
            panel.Children.Add(labelBlock);
            panel.Children.Add(valueBlock);
        }

        AddRow("名称", item.Name);
        AddRow("类型", item.TypeLabel);
        AddRow("远程路径", item.FullPath);
        AddRow("大小", item.IsDirectory ? "—" : $"{item.SizeLabel}（{item.SizeBytes:N0} 字节）");
        AddRow("修改时间", item.ModifiedLabel);
        return panel;
    }

    private void UpdateSelectionState()
    {
        var item = SelectedItem;
        DownloadButton.IsEnabled = _snapshot.CanTransfer && item is { Name: not ".." };
    }

    private sealed record WorkspaceOperationStatusPresentation(
        bool ShowsInlineMessage,
        bool ShowsListingIndicator,
        bool IsTransferring,
        bool ClearsAfterDelay,
        string Message,
        string? ToolTip)
    {
        public static WorkspaceOperationStatusPresentation From(SftpSessionSnapshot snapshot)
        {
            var presentation = snapshot.State switch
            {
                SftpSessionState.ListingDirectory => new WorkspaceOperationStatusPresentation(
                    ShowsInlineMessage: true,
                    ShowsListingIndicator: true,
                    IsTransferring: false,
                    ClearsAfterDelay: false,
                    snapshot.StatusMessage,
                    snapshot.StatusMessage),
                SftpSessionState.Failed => Persistent(snapshot.ErrorMessage ?? snapshot.StatusMessage),
                _ when !string.IsNullOrWhiteSpace(snapshot.StatusMessage) => Transient(snapshot.StatusMessage),
                _ => Hidden
            };
            // 传输在自己的轴上进行，与浏览状态叠加呈现。
            return presentation with { IsTransferring = snapshot.Transfer.IsActive };
        }

        public static WorkspaceOperationStatusPresentation Persistent(string message) =>
            new(true, false, false, false, message, message);

        public static WorkspaceOperationStatusPresentation Transient(string message) =>
            new(true, false, false, true, message, message);

        private static WorkspaceOperationStatusPresentation Hidden =>
            new(false, false, false, false, string.Empty, null);
    }

    private static Microsoft.UI.Xaml.Data.Binding CreateOneWayBinding(string propertyName) => new()
    {
        Path = new Microsoft.UI.Xaml.PropertyPath(propertyName),
        Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
    };
}
