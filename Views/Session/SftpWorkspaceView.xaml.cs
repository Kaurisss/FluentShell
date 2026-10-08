using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Views.Shell;
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

public sealed partial class SftpWorkspaceView : UserControl, ISftpWorkspaceView, ISftpPaneTransferView
{
    private UserPreferences _preferences = new();
    private string _downloadDirectory = AppSettings.DefaultDownloadDirectory;
    public void SetPreferences(UserPreferences preferences, string downloadDirectory)
    {
        var refresh = _preferences.ShowHiddenFiles != preferences.ShowHiddenFiles;
        _preferences = preferences;
        _downloadDirectory = downloadDirectory;
        if (refresh) { RenderDirectoryListing(_snapshot.DirectoryListing); RefreshLocalDirectory(); }
    }
    private readonly IntPtr _windowHandle;
    private readonly ObservableCollection<RemoteFileItem> _remoteFiles = [];
    private bool _isFailureDialogOpen;
    private MenuFlyout? _emptyAreaMenu;
    private SftpSessionSnapshot _snapshot = new(
        SftpSessionState.Idle,
        SftpDirectoryListing.Empty("/"),
        false,
        false,
        false,
        string.Empty,
        null);

    public SftpWorkspaceView(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        InitializeComponent();
        ConfigureRemoteTable();
        InitializeLocalPane();
    }

    public RemoteFileItem? SelectedItem => RemoteTable.SelectedItem as RemoteFileItem;

    public event EventHandler? RefreshRequested;
    public event EventHandler<string>? NavigateRequested;
    public event EventHandler? NewFolderRequested;
    public event EventHandler? UploadRequested;
    public event EventHandler? UploadFolderRequested;
    public event EventHandler<RemoteFileItem>? DownloadRequested;
    public event EventHandler<RemoteFileItem>? RenameRequested;
    public event EventHandler<RemoteFileItem>? DeleteRequested;

    public void Render(SftpSessionSnapshot snapshot)
    {
        var previousState = _snapshot.State;
        var previousListing = _snapshot.DirectoryListing;
        _snapshot = snapshot;

        if (!ReferenceEquals(previousListing, snapshot.DirectoryListing))
            RenderDirectoryListing(snapshot.DirectoryListing);
        else if (snapshot.State == SftpSessionState.Failed)
            PathBox.Text = snapshot.DirectoryListing.Path;

        var isListing = snapshot.State == SftpSessionState.ListingDirectory;
        DirectoryLoadingProgress.IsIndeterminate = isListing;
        DirectoryLoadingOverlay.Visibility = isListing ? Visibility.Visible : Visibility.Collapsed;
        PathBox.IsEnabled = snapshot.CanNavigate;
        RemoteTable.IsEnabled = snapshot.CanNavigate;
        foreach (var button in Toolbar.Children.OfType<Button>()) button.IsEnabled = snapshot.CanNavigate;
        UploadButton.IsEnabled = snapshot.CanTransfer;
        UpdateSelectionState();

        // 读取目录和文件操作失败均以对话框展示完整错误；同一失败的后续快照不重复弹窗。
        if (snapshot.State == SftpSessionState.Failed &&
            previousState != SftpSessionState.Failed)
        {
            _ = ShowFailureDialogAsync(
                snapshot.ErrorMessage ?? snapshot.StatusMessage,
                snapshot.FailureKind == SftpFailureKind.DirectoryRead ? "读取目录失败" : "SFTP 操作失败");
        }

        // Transfer failures remain in the global task center instead of interrupting browsing.
    }

    private void RenderDirectoryListing(SftpDirectoryListing listing)
    {
        // 就地增删，不重新赋值 ItemsSource：整体重绑会让表格重算列宽，而填充剩余宽度的
        // 那一列填不满，表头右侧会留下大片空白。
        RemoteTable.SelectedItem = null;
        _remoteFiles.Clear();
        foreach (var item in listing.Items.Where(item => _preferences.ShowHiddenFiles || item.Name == ".." || !item.Name.StartsWith('.'))) _remoteFiles.Add(item);
        PathBox.Text = listing.Path;
    }

    private async Task ShowFailureDialogAsync(string message, string title = "SFTP 操作失败")
    {
        if (_isFailureDialogOpen || string.IsNullOrWhiteSpace(message) || XamlRoot is null) return;

        _isFailureDialogOpen = true;
        try
        {
            await ShellDialogService.ShowMessageAsync(XamlRoot, title, message);
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

    public async Task<SftpUploadDirectory?> PickUploadFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        return folder is null ? null : new SftpUploadDirectory(folder.Name, folder.Path);
    }

    public async Task<string?> PickDownloadDirectoryAsync()
    {
        if (_preferences.UseDefaultDownloadDirectory) return _downloadDirectory;
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
        // Observe releases even when a cell or scroll viewer handles the routed event.
        RemoteTable.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(RemoteTable_PointerReleased), true);
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
            MinimumWidth = 120
        });
        RemoteTable.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "类型", MappingName = nameof(RemoteFileItem.TypeLabel),
            CellTemplate = (DataTemplate)Resources["SftpTypeCellTemplate"], Width = 64
        });
        RemoteTable.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "大小", MappingName = nameof(RemoteFileItem.SizeBytes),
            CellTemplate = (DataTemplate)Resources["SftpSizeCellTemplate"], Width = 76
        });
        RemoteTable.Columns.Add(new GridTemplateColumn
        {
            HeaderText = "修改时间", MappingName = nameof(RemoteFileItem.ModifiedAt),
            CellTemplate = (DataTemplate)Resources["SftpModifiedCellTemplate"], Width = 148,
            HeaderStyle = (Style)Resources["LastRemoteHeaderStyle"]
        });

    }

    private MenuFlyout BuildRemoteRowMenu()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开文件夹" };
        open.Click += (_, _) => OpenSelectedDirectory();
        var download = new MenuFlyoutItem { Text = "下载" };
        download.Click += (_, _) => RequestDownloadToLocal();
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
        var refresh = new MenuFlyoutItem { Text = "刷新" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
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
            refresh.IsEnabled = _snapshot.CanNavigate;
            open.IsEnabled = _snapshot.CanNavigate && item?.IsDirectory == true;
            download.IsEnabled = _snapshot.CanTransfer && item is { Name: not ".." } && _localPath is not null;
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
        var upload = new MenuFlyoutItem { Text = "上传文件" };
        upload.Click += (_, _) => UploadRequested?.Invoke(this, EventArgs.Empty);
        var uploadFolder = new MenuFlyoutItem { Text = "上传文件夹" };
        uploadFolder.Click += (_, _) => UploadFolderRequested?.Invoke(this, EventArgs.Empty);
        var newFolder = new MenuFlyoutItem { Text = "新建文件夹" };
        newFolder.Click += (_, _) => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(upload);
        menu.Items.Add(uploadFolder);
        menu.Items.Add(newFolder);
        ApplyChineseMenuFont(menu);
        menu.Opened += (_, _) =>
        {
            refresh.IsEnabled = _snapshot.CanNavigate;
            upload.IsEnabled = _snapshot.CanTransfer;
            uploadFolder.IsEnabled = _snapshot.CanTransfer;
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
        if (e.OriginalSource is DependencyObject source && IsOnRowOrChrome(source, RemoteTable)) return;

        e.Handled = true;
        _emptyAreaMenu?.ShowAt(RemoteTable, e.GetPosition(RemoteTable));
    }

    private static bool IsOnRowOrChrome(DependencyObject source, SfDataGrid table)
    {
        for (var current = source;
             current is not null && !ReferenceEquals(current, table);
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

    private void UploadButton_Click(object sender, RoutedEventArgs e) =>
        UploadRequested?.Invoke(this, EventArgs.Empty);

    private void UploadFolder_Click(object sender, RoutedEventArgs e) =>
        UploadFolderRequested?.Invoke(this, EventArgs.Empty);

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

    private void RemoteTable_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RemoteTable).Properties.PointerUpdateKind !=
            Microsoft.UI.Input.PointerUpdateKind.XButton1Released) return;

        e.Handled = true;
        if (_snapshot.CanNavigate && _snapshot.DirectoryListing.Path != "/")
            NavigateRequested?.Invoke(this, "..");
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

}
