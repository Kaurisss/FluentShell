using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Renci.SshNet.Common;
using NovaShell.Models;
using NovaShell.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUI.TableView;
using WinRT.Interop;

namespace NovaShell.Views;

public sealed class SessionWorkspace : UserControl
{
    private readonly ServerProfile _profile;
    private readonly IntPtr _windowHandle;
    private readonly Func<HostFingerprintRequiredEventArgs, Task<bool>> _fingerprintConfirmation;
    private readonly Func<Task<string?>> _passwordProvider;
    private readonly ElementTheme _workspaceTheme;
    private readonly ObservableCollection<RemoteFileItem> _remoteFiles = [];
    private readonly List<string> _history = [];
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly WebView2 _terminalView = new();
    private readonly StringBuilder _pendingTerminalOutput = new();
    private readonly TextBox _composer = new();
    private readonly PasswordBox _secureComposer = new();
    private readonly TextBlock _connectionState = new();
    private readonly Button _reconnectButton = new();
    private readonly TextBox _sftpPathBox = new();
    private readonly ProgressRing _directoryProgress = new();
    private readonly TextBlock _directoryStatus = new();
    private readonly TableView _remoteTable = new();
    private readonly TextBlock _sftpSelectionStatus = new();
    private readonly AppBarButton _downloadButton = new();
    private readonly AppBarButton _renameButton = new();
    private readonly AppBarButton _deleteButton = new();
    private readonly ProgressBar _transferProgress = new();
    private readonly TextBlock _transferStatus = new();
    private readonly Button _cancelTransferButton = new();
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly Grid _sftpGrid = new();
    private readonly Grid _workspaceGrid = new();
    private readonly Button _sftpRestoreButton = new();
    private readonly CommandBar _sftpToolbar = new();
    private string _currentPath = "/";
    private bool _showHiddenFiles;
    private bool _isCollapsed;
    private bool _terminalInitializationStarted;
    private bool _terminalReady;
    private double _terminalFontSize = 14;
    private double _previousSftpHeight = 260;
    private CancellationTokenSource? _metricsCts;
    private CancellationTokenSource? _transferCts;
    private bool _isConnecting;
    private bool _isDirectoryLoading;

    public SessionWorkspace(ServerProfile profile, IntPtr windowHandle, Func<HostFingerprintRequiredEventArgs, Task<bool>> fingerprintConfirmation, Func<Task<string?>> passwordProvider, ElementTheme workspaceTheme)
    {
        _profile = profile;
        _windowHandle = windowHandle;
        _fingerprintConfirmation = fingerprintConfirmation;
        _passwordProvider = passwordProvider;
        _workspaceTheme = workspaceTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        RequestedTheme = _workspaceTheme;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _showHiddenFiles = profile.ShowHiddenFiles;
        Content = BuildLayout();
    }

    public ServerProfile Profile => _profile;
    public string DisplayTitle => _profile.Name;
    public bool IsConnected => _activeService?.IsConnected == true;
    public bool IsTransferActive => _transferCts is not null;
    public event EventHandler<ServerMetrics?>? MetricsUpdated;
    public event EventHandler<string>? StatusChanged;

    private Brush ThemeBrush(string key)
    {
        var themeKey = _workspaceTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary) && dictionary is ResourceDictionary themeDictionary && themeDictionary.ContainsKey(key))
            return (Brush)themeDictionary[key];
        return (Brush)Application.Current.Resources[key];
    }

    public void SetActive(bool active)
    {
        if (!active) _metricsCts?.Cancel();
        else if (_activeService?.IsConnected == true) _ = RefreshMetricsLoopAsync(_activeService);
    }

    public void SetTerminalFontSize(double value)
    {
        _terminalFontSize = value;
        _composer.FontSize = value;
        _secureComposer.FontSize = value;
        PostTerminalMessage(new { type = "fontSize", value });
    }

    private UIElement BuildLayout()
    {
        var root = _workspaceGrid;
        // The window backdrop should remain visible around the terminal and SFTP surfaces.
        root.Background = null;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(65, GridUnitType.Star), MinHeight = 180 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35, GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var terminalGrid = BuildTerminalGrid();
        Grid.SetRow(terminalGrid, 0);
        root.Children.Add(terminalGrid);

        var splitter = new Thumb { Background = ThemeBrush("SubtleStrokeBrush"), Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.DragDelta += Splitter_DragDelta;
        splitter.DoubleTapped += (_, _) => ToggleSftp();
        Grid.SetRow(splitter, 1);
        root.Children.Add(splitter);

        var sftpPanel = BuildSftpPanel();
        Grid.SetRow(sftpPanel, 2);
        root.Children.Add(sftpPanel);

        var restoreRow = new Grid { Padding = new Thickness(12, 4, 12, 4) };
        _sftpRestoreButton.Content = "显示 SFTP 文件管理器";
        _sftpRestoreButton.Click += (_, _) => ToggleSftp();
        _sftpRestoreButton.Visibility = Visibility.Collapsed;
        restoreRow.Children.Add(_sftpRestoreButton);
        Grid.SetRow(restoreRow, 3);
        root.Children.Add(restoreRow);
        return root;
    }

    private Grid BuildTerminalGrid()
    {
        var grid = new Grid { Padding = new Thickness(14, 0, 14, 8), RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _terminalView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _terminalView.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_terminalView, 0);
        grid.Children.Add(_terminalView);
        _terminalView.Loaded += TerminalView_Loaded;

        var statePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
        _connectionState.Text = "未连接";
        _connectionState.Foreground = ThemeBrush("MutedTextBrush");
        _connectionState.VerticalAlignment = VerticalAlignment.Center;
        statePanel.Children.Add(_connectionState);
        _reconnectButton.Content = "重新连接";
        _reconnectButton.Visibility = Visibility.Collapsed;
        _reconnectButton.Click += async (_, _) => await ConnectAsync();
        statePanel.Children.Add(_reconnectButton);
        Grid.SetRow(statePanel, 0);
        Canvas.SetZIndex(statePanel, 1);
        grid.Children.Add(statePanel);

        var composerBorder = new Border { Background = ThemeBrush("PageSurfaceBrush"), BorderBrush = ThemeBrush("SubtleStrokeBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 4) };
        var composerGrid = new Grid { ColumnSpacing = 6 };
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++) composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _composer.PlaceholderText = "增强输入：Enter 发送，Shift+Enter 换行；历史仅保留在本次会话";
        _composer.AcceptsReturn = true;
        _composer.TextWrapping = TextWrapping.Wrap;
        _composer.MinHeight = 40;
        _composer.KeyDown += Composer_KeyDown;
        composerGrid.Children.Add(_composer);
        _secureComposer.PlaceholderText = "隐藏输入：内容不会进入历史记录";
        _secureComposer.Visibility = Visibility.Collapsed;
        _secureComposer.KeyDown += SecureComposer_KeyDown;
        composerGrid.Children.Add(_secureComposer);
        var secureToggle = new ToggleButton
        {
            Content = CreateCommandIcon(Symbol.HideBcc),
            Style = (Style)Application.Current.Resources["TitleBarSessionTabStyle"],
            Width = 40,
            Height = 40,
            MinWidth = 40,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTipService.SetToolTip(secureToggle, "隐藏输入");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(secureToggle, "隐藏输入");
        Grid.SetColumn(secureToggle, 1);
        composerGrid.Children.Add(secureToggle);
        var pasteButton = CreateCommandButton(Symbol.Paste, "粘贴");
        Grid.SetColumn(pasteButton, 2);
        composerGrid.Children.Add(pasteButton);
        var historyButton = CreateCommandButton(Symbol.Clock, "本次会话历史");
        historyButton.Click += HistoryButton_Click;
        Grid.SetColumn(historyButton, 3);
        composerGrid.Children.Add(historyButton);
        var clearButton = CreateCommandButton(Symbol.Clear, "清空输入");
        clearButton.Click += (_, _) => { _composer.Text = string.Empty; _secureComposer.Password = string.Empty; };
        Grid.SetColumn(clearButton, 4);
        composerGrid.Children.Add(clearButton);
        var sendButton = new Button
        {
            Content = CreateCommandIcon(Symbol.Send),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Width = 40,
            Height = 40,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(sendButton, "发送命令");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sendButton, "发送命令");
        sendButton.Click += async (_, _) => await SendComposerAsync();
        Grid.SetColumn(sendButton, 5);
        composerGrid.Children.Add(sendButton);
        secureToggle.Checked += (_, _) =>
        {
            _composer.Visibility = Visibility.Collapsed;
            _secureComposer.Visibility = Visibility.Visible;
            historyButton.IsEnabled = false;
            pasteButton.IsEnabled = false;
            _secureComposer.Focus(FocusState.Programmatic);
        };
        secureToggle.Unchecked += (_, _) =>
        {
            _secureComposer.Password = string.Empty;
            _secureComposer.Visibility = Visibility.Collapsed;
            _composer.Visibility = Visibility.Visible;
            historyButton.IsEnabled = true;
            pasteButton.IsEnabled = true;
            _composer.Focus(FocusState.Programmatic);
        };
        pasteButton.Click += async (_, _) =>
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) _composer.Text += await content.GetTextAsync();
        };
        composerBorder.Child = composerGrid;
        Grid.SetRow(composerBorder, 1);
        grid.Children.Add(composerBorder);
        return grid;
    }

    private void TerminalView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_terminalInitializationStarted) return;
        _terminalInitializationStarted = true;
        _ = InitializeTerminalAsync();
    }

    private async Task InitializeTerminalAsync()
    {
        try
        {
            await _terminalView.EnsureCoreWebView2Async();
            var terminalAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            _terminalView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _terminalView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _terminalView.CoreWebView2.SetVirtualHostNameToFolderMapping("novashell.local", terminalAssets, CoreWebView2HostResourceAccessKind.Allow);
            _terminalView.CoreWebView2.WebMessageReceived += TerminalView_WebMessageReceived;
            _terminalView.Source = new Uri("https://novashell.local/index.html");
        }
        catch (Exception ex)
        {
            AppendOutput($"\r\n[终端初始化失败] {ex.Message}\r\n");
        }
    }

    private void TerminalView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();
            switch (type)
            {
                case "ready":
                    _terminalReady = true;
                    PostTerminalMessage(new { type = "fontSize", value = _terminalFontSize });
                    if (_pendingTerminalOutput.Length > 0)
                    {
                        var pending = _pendingTerminalOutput.ToString();
                        _pendingTerminalOutput.Clear();
                        PostTerminalMessage(new { type = "write", data = pending });
                    }
                    break;
                case "input":
                    if (root.TryGetProperty("data", out var input) && input.GetString() is string data && data.Length > 0) _ = SendTerminalInputAsync(data);
                    break;
                case "resize":
                    if (root.TryGetProperty("cols", out var cols) && root.TryGetProperty("rows", out var rows)) _ = ResizeTerminalAsync(cols.GetInt32(), rows.GetInt32());
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendOutput($"\r\n[终端消息失败] {ex.Message}\r\n");
        }
    }

    private void PostTerminalMessage(object message)
    {
        if (!_terminalReady || _terminalView.CoreWebView2 is null) return;
        _terminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task SendTerminalInputAsync(string data)
    {
        if (_activeService is null || !_activeService.IsConnected) return;
        try { await _activeService.SendRawAsync(data); }
        catch (Exception ex) { AppendOutput($"\r\n[发送失败] {ex.Message}\r\n"); }
    }

    private async Task ResizeTerminalAsync(int columns, int rows)
    {
        if (_activeService is null || !_activeService.IsConnected || columns <= 0 || rows <= 0) return;
        try { await _activeService.ResizeTerminalAsync(columns, rows); } catch { }
    }

    private Grid BuildSftpPanel()
    {
        var panel = new Grid { Padding = new Thickness(14, 8, 14, 10), RowSpacing = 6 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { ColumnSpacing = 10 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(new TextBlock { Text = "SFTP 文件管理器", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _sftpSelectionStatus.Text = "0 项";
        _sftpSelectionStatus.FontSize = 12;
        _sftpSelectionStatus.Foreground = ThemeBrush("MutedTextBrush");
        _sftpSelectionStatus.VerticalAlignment = VerticalAlignment.Center;
        headingText.Children.Add(_sftpSelectionStatus);
        heading.Children.Add(headingText);

        _sftpToolbar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _sftpToolbar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed;
        _sftpToolbar.IsDynamicOverflowEnabled = true;
        var up = CreateAppBarButton("上一级", Symbol.Up);
        up.Click += async (_, _) => await NavigateUpAsync();
        _sftpToolbar.PrimaryCommands.Add(up);
        var refresh = CreateAppBarButton("刷新", Symbol.Refresh);
        refresh.Click += async (_, _) => await RefreshRemoteFilesAsync();
        _sftpToolbar.PrimaryCommands.Add(refresh);
        var newFolder = CreateAppBarButton("新建文件夹", Symbol.Add);
        newFolder.Click += NewFolderButton_Click;
        _sftpToolbar.PrimaryCommands.Add(newFolder);
        var upload = CreateAppBarButton("上传", Symbol.Upload);
        upload.Click += UploadButton_Click;
        _sftpToolbar.PrimaryCommands.Add(upload);
        ConfigureSelectionCommand(_downloadButton, "下载", Symbol.Download, DownloadButton_Click);
        ConfigureSelectionCommand(_renameButton, "重命名", Symbol.Edit, RenameButton_Click);
        ConfigureSelectionCommand(_deleteButton, "删除", Symbol.Delete, DeleteButton_Click);
        _sftpToolbar.PrimaryCommands.Add(_downloadButton);
        _sftpToolbar.PrimaryCommands.Add(_renameButton);
        _sftpToolbar.PrimaryCommands.Add(_deleteButton);
        var hidden = new AppBarToggleButton { Label = "显示隐藏文件", Icon = CreateCommandIcon(Symbol.View), IsChecked = _showHiddenFiles };
        ToolTipService.SetToolTip(hidden, "显示隐藏文件");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(hidden, "显示隐藏文件");
        hidden.Click += (_, _) =>
        {
            _showHiddenFiles = hidden.IsChecked == true;
            _profile.ShowHiddenFiles = _showHiddenFiles;
            _ = RefreshRemoteFilesAsync();
        };
        _sftpToolbar.PrimaryCommands.Add(hidden);
        Grid.SetColumn(_sftpToolbar, 1);
        heading.Children.Add(_sftpToolbar);

        var collapse = CreateCommandButton(Symbol.ClosePane, "收起 SFTP 文件管理器");
        collapse.Click += (_, _) => ToggleSftp();
        Grid.SetColumn(collapse, 2);
        heading.Children.Add(collapse);
        Grid.SetRow(heading, 0);
        panel.Children.Add(heading);

        var pathBar = new Grid();
        pathBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _sftpPathBox.Text = "/";
        _sftpPathBox.PlaceholderText = "远程路径";
        _sftpPathBox.FontFamily = new FontFamily("Cascadia Mono");
        _sftpPathBox.FontSize = 12;
        _sftpPathBox.IsSpellCheckEnabled = false;
        _sftpPathBox.KeyDown += SftpPathBox_KeyDown;
        pathBar.Children.Add(_sftpPathBox);
        var directoryStatusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _directoryProgress.Width = 16;
        _directoryProgress.Height = 16;
        _directoryProgress.IsActive = false;
        _directoryProgress.Visibility = Visibility.Collapsed;
        _directoryStatus.FontSize = 12;
        _directoryStatus.Foreground = ThemeBrush("MutedTextBrush");
        _directoryStatus.MaxWidth = 220;
        _directoryStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        directoryStatusPanel.Children.Add(_directoryProgress);
        directoryStatusPanel.Children.Add(_directoryStatus);
        Grid.SetColumn(directoryStatusPanel, 1);
        pathBar.Children.Add(directoryStatusPanel);
        Grid.SetRow(pathBar, 1);
        panel.Children.Add(pathBar);

        ConfigureRemoteTable();
        Grid.SetRow(_remoteTable, 2);
        panel.Children.Add(_remoteTable);

        _transferStatus.Text = "暂无文件传输";
        _transferStatus.FontSize = 12;
        _transferStatus.Foreground = ThemeBrush("MutedTextBrush");
        _transferStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _transferProgress.Visibility = Visibility.Collapsed;
        _transferProgress.IsIndeterminate = true;
        _transferProgress.Width = 160;
        var transferRow = new Grid { ColumnSpacing = 10 };
        transferRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transferRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transferRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transferRow.Children.Add(_transferStatus);
        Grid.SetColumn(_transferProgress, 1);
        transferRow.Children.Add(_transferProgress);
        _cancelTransferButton.Content = CreateCommandIcon(Symbol.Cancel);
        _cancelTransferButton.Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"];
        _cancelTransferButton.Width = 40;
        _cancelTransferButton.Height = 40;
        ToolTipService.SetToolTip(_cancelTransferButton, "取消文件传输");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_cancelTransferButton, "取消文件传输");
        _cancelTransferButton.Visibility = Visibility.Collapsed;
        _cancelTransferButton.Click += (_, _) => _transferCts?.Cancel();
        Grid.SetColumn(_cancelTransferButton, 2);
        transferRow.Children.Add(_cancelTransferButton);
        Grid.SetRow(transferRow, 3);
        panel.Children.Add(transferRow);
        UpdateSftpSelectionState();
        return panel;
    }

    private void ConfigureRemoteTable()
    {
        _remoteTable.ItemsSource = _remoteFiles;
        _remoteTable.AutoGenerateColumns = false;
        _remoteTable.IsReadOnly = true;
        _remoteTable.SelectionMode = ListViewSelectionMode.Single;
        _remoteTable.SelectionUnit = TableViewSelectionUnit.Row;
        _remoteTable.CornerButtonMode = TableViewCornerButtonMode.Options;
        _remoteTable.CanSortColumns = true;
        _remoteTable.CanFilterColumns = true;
        _remoteTable.CanResizeColumns = true;
        _remoteTable.CanReorderColumns = true;
        _remoteTable.GridLinesVisibility = TableViewGridLinesVisibility.Horizontal;
        _remoteTable.HeaderGridLinesVisibility = TableViewGridLinesVisibility.Horizontal;
        _remoteTable.HorizontalGridLinesStroke = ThemeBrush("SubtleStrokeBrush");
        _remoteTable.HeaderRowHeight = 36;
        _remoteTable.RowHeight = 36;
        _remoteTable.FontSize = 13;
        _remoteTable.RowDoubleTapped += RemoteTable_RowDoubleTapped;
        _remoteTable.CellDoubleTapped += RemoteTable_CellDoubleTapped;
        _remoteTable.RowContextFlyoutOpening += (_, args) => _remoteTable.SelectedItem = args.Item;
        _remoteTable.SelectionChanged += RemoteTable_SelectionChanged;
        // TableView handles Enter for its own navigation first. Listen to handled key events
        // as well so Enter can open the selected directory in the workspace.
        _remoteTable.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RemoteTable_KeyDown), true);
        _remoteTable.RowContextFlyout = BuildRemoteRowMenu();

        var iconStyle = new Style(typeof(TextBlock));
        iconStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons")));
        iconStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 16d));
        iconStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        _remoteTable.Columns.Add(new TableViewTextColumn
        {
            Header = string.Empty,
            Binding = CreateOneWayBinding(nameof(RemoteFileItem.IconGlyph)),
            Width = new GridLength(42),
            CanResize = false,
            CanSort = false,
            CanFilter = false,
            CanReorder = false,
            ElementStyle = iconStyle
        });
        _remoteTable.Columns.Add(new TableViewTextColumn
        {
            Header = "名称",
            Binding = CreateOneWayBinding(nameof(RemoteFileItem.Name)),
            SortMemberPath = nameof(RemoteFileItem.SortName),
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 200
        });
        _remoteTable.Columns.Add(new TableViewTextColumn
        {
            Header = "类型",
            Binding = CreateOneWayBinding(nameof(RemoteFileItem.TypeLabel)),
            Width = new GridLength(90),
            MinWidth = 72
        });
        _remoteTable.Columns.Add(new TableViewTextColumn
        {
            Header = "大小",
            Binding = CreateOneWayBinding(nameof(RemoteFileItem.SizeLabel)),
            SortMemberPath = nameof(RemoteFileItem.SizeBytes),
            Width = new GridLength(110),
            MinWidth = 88
        });
        _remoteTable.Columns.Add(new TableViewTextColumn
        {
            Header = "修改时间",
            Binding = CreateOneWayBinding(nameof(RemoteFileItem.ModifiedLabel)),
            SortMemberPath = nameof(RemoteFileItem.ModifiedAt),
            Width = new GridLength(164),
            MinWidth = 140
        });
    }

    private MenuFlyout BuildRemoteRowMenu()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开文件夹" };
        open.Click += async (_, _) => { if (SelectedRemoteItem is { IsDirectory: true } item) await NavigateToAsync(item.FullPath); };
        var download = new MenuFlyoutItem { Text = "下载" };
        download.Click += async (_, _) => await DownloadSelectedAsync();
        var copyPath = new MenuFlyoutItem { Text = "复制远程路径" };
        copyPath.Click += (_, _) => CopySelectedRemotePath();
        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += async (_, _) => await RenameSelectedAsync();
        var delete = new MenuFlyoutItem { Text = "删除" };
        delete.Click += async (_, _) => await DeleteSelectedAsync();
        menu.Items.Add(open);
        menu.Items.Add(download);
        menu.Items.Add(copyPath);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Opened += (_, _) =>
        {
            var item = SelectedRemoteItem;
            open.IsEnabled = item?.IsDirectory == true;
            download.IsEnabled = item is { IsDirectory: false };
            copyPath.IsEnabled = item is not null;
            rename.IsEnabled = item is not null && item.Name != "..";
            delete.IsEnabled = item is not null && item.Name != "..";
        };
        return menu;
    }

    private static Binding CreateOneWayBinding(string propertyName) => new() { Path = new PropertyPath(propertyName), Mode = BindingMode.OneWay };

    private static SymbolIcon CreateCommandIcon(Symbol symbol) => new() { Symbol = symbol };

    private static Button CreateCommandButton(Symbol symbol, string title)
    {
        var button = new Button
        {
            Content = CreateCommandIcon(symbol),
            Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"],
            Width = 40,
            Height = 40
        };
        ToolTipService.SetToolTip(button, title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        return button;
    }

    private static AppBarButton CreateAppBarButton(string label, Symbol symbol)
    {
        var button = new AppBarButton { Label = label, Icon = CreateCommandIcon(symbol) };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private static void ConfigureSelectionCommand(AppBarButton button, string label, Symbol symbol, RoutedEventHandler handler)
    {
        button.Label = label;
        button.Icon = CreateCommandIcon(symbol);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.IsEnabled = false;
        button.Click += handler;
    }

    public async Task ConnectAsync()
    {
        if (_isConnecting || IsConnected) return;
        _isConnecting = true;
        _connectionState.Text = "连接中……";
        _reconnectButton.Visibility = Visibility.Collapsed;
        StatusChanged?.Invoke(this, "连接中");
        try
        {
            var secret = await _passwordProvider();
            if (secret is null)
            {
                _connectionState.Text = "已取消";
                _reconnectButton.Visibility = Visibility.Visible;
                StatusChanged?.Invoke(this, "连接已取消");
                return;
            }
            await ConnectWithSecretAsync(secret);
        }
        catch (Exception ex)
        {
            _connectionState.Text = "连接失败";
            _reconnectButton.Visibility = Visibility.Visible;
            StatusChanged?.Invoke(this, $"连接失败：{ex.Message}");
            AppendOutput($"\r\n[连接失败] {ex.Message}\r\n");
        }
        finally { _isConnecting = false; }
    }

    private async Task ConnectWithSecretAsync(string secret)
    {
        if (_activeService is not null)
        {
            _activeService.OutputReceived -= Connection_OutputReceived;
            _activeService.Disconnected -= Connection_Disconnected;
            await _activeService.DisposeAsync();
        }
        var service = new SshConnectionService(_profile, secret);
        _activeService = service;
        service.OutputReceived += Connection_OutputReceived;
        service.HostFingerprintRequired += Connection_HostFingerprintRequired;
        service.Disconnected += Connection_Disconnected;
        try { await service.ConnectAsync(); }
        catch { _activeService = null; await service.DisposeAsync(); throw; }
        _connectionState.Text = "已连接";
        _reconnectButton.Visibility = Visibility.Collapsed;
        StatusChanged?.Invoke(this, "已连接");
        AppendOutput("连接主机成功。\r\n");
        await RefreshRemoteFilesAsync(service);
        PostTerminalMessage(new { type = "focus" });
        _ = RefreshMetricsLoopAsync(service);
    }

    private void Connection_HostFingerprintRequired(object? sender, HostFingerprintRequiredEventArgs e)
    {
        var signal = new ManualResetEventSlim(false);
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try { e.Accepted = await _fingerprintConfirmation(e); }
            finally { signal.Set(); }
        });
        signal.Wait(TimeSpan.FromMinutes(2));
        if (e.Accepted) _profile.HostFingerprint = e.Fingerprint;
    }

    private void Connection_OutputReceived(object? sender, string e)
    {
        _dispatcherQueue.TryEnqueue(() => AppendOutput(e));
    }

    private void Connection_Disconnected(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _connectionState.Text = "已断开";
            _reconnectButton.Visibility = Visibility.Visible;
            StatusChanged?.Invoke(this, "连接已断开");
            AppendOutput("\r\n[连接已断开]\r\n");
        });
    }

    private void AppendOutput(string text)
    {
        if (!_terminalReady)
        {
            _pendingTerminalOutput.Append(text);
            if (_pendingTerminalOutput.Length > 1_000_000)
                _pendingTerminalOutput.Remove(0, _pendingTerminalOutput.Length - 800_000);
            return;
        }
        PostTerminalMessage(new { type = "write", data = text });
    }

    private async void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !IsShiftDown()) { e.Handled = true; await SendFromBoxAsync(_composer, true); }
    }

    private async void SecureComposer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await SendComposerAsync(); }
    }

    private async Task SendComposerAsync()
    {
        if (_secureComposer.Visibility == Visibility.Visible)
        {
            var command = _secureComposer.Password;
            if (string.IsNullOrEmpty(command)) return;
            try { await _activeService!.SendAsync(command, true); _secureComposer.Password = string.Empty; }
            catch (Exception ex) { AppendOutput($"\r\n[发送失败] {ex.Message}\r\n"); }
            return;
        }
        await SendFromBoxAsync(_composer, true);
    }

    private bool IsShiftDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private async Task SendFromBoxAsync(TextBox box, bool appendNewLine)
    {
        var command = box.Text;
        if (string.IsNullOrWhiteSpace(command)) return;
        try
        {
            // The currently connected service is injected when the session is created in MainWindow.
            await _activeService!.SendAsync(command, appendNewLine);
            _history.Add(command);
            box.Text = string.Empty;
        }
        catch (Exception ex) { AppendOutput($"\r\n[发送失败] {ex.Message}\r\n"); }
    }

    private SshConnectionService? _activeService;

    private async Task RefreshMetricsLoopAsync(SshConnectionService service)
    {
        _activeService = service;
        var previous = _metricsCts;
        var current = new CancellationTokenSource();
        _metricsCts = current;
        previous?.Cancel();
        previous?.Dispose();
        while (!current.IsCancellationRequested && service.IsConnected)
        {
            var metrics = await service.ReadLinuxMetricsAsync(current.Token);
            MetricsUpdated?.Invoke(this, metrics);
            try { await Task.Delay(TimeSpan.FromSeconds(3), current.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private RemoteFileItem? SelectedRemoteItem => _remoteTable.SelectedItem as RemoteFileItem;

    private void RemoteTable_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSftpSelectionState();

    private async void RemoteTable_RowDoubleTapped(object? sender, TableViewRowDoubleTappedEventArgs e)
    {
        await OpenDirectoryAsync(e.Item as RemoteFileItem);
    }

    private async void RemoteTable_CellDoubleTapped(object? sender, TableViewCellDoubleTappedEventArgs e)
    {
        await OpenDirectoryAsync(e.Item as RemoteFileItem);
    }

    private async Task OpenDirectoryAsync(RemoteFileItem? item)
    {
        if (item is not { IsDirectory: true } || _isDirectoryLoading) return;
        await NavigateToAsync(item.FullPath);
    }

    private async void RemoteTable_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && SelectedRemoteItem is { IsDirectory: true } directory)
        {
            e.Handled = true;
            await NavigateToAsync(directory.FullPath);
        }
        else if (e.Key == Windows.System.VirtualKey.F2 && SelectedRemoteItem is not null)
        {
            e.Handled = true;
            await RenameSelectedAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Delete && SelectedRemoteItem is not null)
        {
            e.Handled = true;
            await DeleteSelectedAsync();
        }
    }

    private async void SftpPathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await NavigateToAsync(_sftpPathBox.Text);
    }

    private void CopySelectedRemotePath()
    {
        if (SelectedRemoteItem is not { } item) return;
        var package = new DataPackage();
        package.SetText(item.FullPath);
        Clipboard.SetContent(package);
        _transferStatus.Text = "已复制远程路径";
    }

    private void UpdateSftpSelectionState()
    {
        var item = SelectedRemoteItem;
        _downloadButton.IsEnabled = item is { IsDirectory: false };
        _renameButton.IsEnabled = item is not null && item.Name != "..";
        _deleteButton.IsEnabled = item is not null && item.Name != "..";
        var fileCount = _remoteFiles.Count(remote => remote.Name != "..");
        _sftpSelectionStatus.Text = item is null ? $"{fileCount} 项" : $"已选择 {item.Name}";
        ToolTipService.SetToolTip(_sftpSelectionStatus, item?.FullPath);
    }

    private void SetDirectoryLoading(bool isLoading, string? status = null)
    {
        _isDirectoryLoading = isLoading;
        _directoryProgress.IsActive = isLoading;
        _directoryProgress.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        _directoryStatus.Text = status ?? (isLoading ? "正在读取目录…" : string.Empty);
        if (isLoading) ToolTipService.SetToolTip(_directoryStatus, null);
        _sftpPathBox.IsEnabled = !isLoading;
        _remoteTable.IsEnabled = !isLoading;
        _sftpToolbar.IsEnabled = !isLoading;
    }

    private async Task<bool> RefreshRemoteFilesAsync() => await RefreshRemoteFilesAsync(_activeService);

    private async Task<bool> RefreshRemoteFilesAsync(SshConnectionService? service)
    {
        if (service?.SftpClient is null || !service.SftpClient.IsConnected || _isDirectoryLoading) return false;
        var path = _currentPath;
        SetDirectoryLoading(true, $"正在读取 {path}…");
        var succeeded = false;
        try
        {
            // Keep the existing rows on screen until the remote request completes.
            var items = await Task.Run(() => service.SftpClient.ListDirectory(path)
                .Where(item => item.Name is not "." and not ".." && (_showHiddenFiles || !item.Name.StartsWith('.')))
                .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new RemoteFileItem
                {
                    Name = item.Name,
                    IsDirectory = item.IsDirectory,
                    FullPath = item.FullName,
                    TypeLabel = item.IsDirectory ? "目录" : "文件",
                    SizeBytes = item.IsDirectory ? -1 : item.Length,
                    SizeLabel = item.IsDirectory ? "—" : FormatBytes(item.Length),
                    ModifiedAt = item.LastWriteTime.ToLocalTime(),
                    ModifiedLabel = item.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                }).ToList());
            if (path != "/")
            {
                var parent = ParentRemotePath(path);
                items.Insert(0, new RemoteFileItem
                {
                    Name = "..",
                    IsDirectory = true,
                    FullPath = parent,
                    TypeLabel = "目录",
                    SizeBytes = -1,
                    SizeLabel = "—",
                    ModifiedLabel = string.Empty
                });
            }
            _remoteTable.DeselectAll();
            _remoteFiles.Clear();
            foreach (var item in items) _remoteFiles.Add(item);
            _sftpPathBox.Text = path;
            UpdateSftpSelectionState();
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            SetDirectoryLoading(false, "读取失败");
            ToolTipService.SetToolTip(_directoryStatus, ex.Message);
            _transferStatus.Text = $"读取目录失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (succeeded) SetDirectoryLoading(false);
        }
    }

    private async Task NavigateToAsync(string path)
    {
        if (_isDirectoryLoading) return;
        var previous = _currentPath;
        _currentPath = NormalizeRemotePath(path);
        if (!await RefreshRemoteFilesAsync())
        {
            _currentPath = previous;
            _sftpPathBox.Text = previous;
        }
    }

    private async Task NavigateUpAsync()
    {
        if (_isDirectoryLoading || _currentPath == "/") return;
        var previous = _currentPath;
        _currentPath = ParentRemotePath(_currentPath);
        if (!await RefreshRemoteFilesAsync())
        {
            _currentPath = previous;
            _sftpPathBox.Text = previous;
        }
    }

    private string ParentRemotePath(string path)
    {
        var normalized = NormalizeRemotePath(path);
        if (normalized == "/") return "/";
        var parent = normalized.TrimEnd('/');
        var slash = parent.LastIndexOf('/');
        return slash <= 0 ? "/" : parent[..slash];
    }

    private void ToggleSftp()
    {
        _isCollapsed = !_isCollapsed;
        if (_isCollapsed)
        {
            _previousSftpHeight = Math.Max(120, _workspaceGrid.RowDefinitions[2].ActualHeight);
            _workspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            _workspaceGrid.RowDefinitions[1].Height = new GridLength(0);
            _workspaceGrid.RowDefinitions[2].MinHeight = 0;
            _workspaceGrid.RowDefinitions[2].Height = new GridLength(0);
        }
        else
        {
            _workspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            _workspaceGrid.RowDefinitions[1].Height = new GridLength(6);
            _workspaceGrid.RowDefinitions[2].MinHeight = 120;
            _workspaceGrid.RowDefinitions[2].Height = new GridLength(_previousSftpHeight);
        }
        _sftpToolbar.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _sftpRestoreButton.Visibility = _isCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Splitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isCollapsed) return;
        var terminalHeight = _workspaceGrid.RowDefinitions[0].ActualHeight + e.VerticalChange;
        var sftpHeight = _workspaceGrid.RowDefinitions[2].ActualHeight - e.VerticalChange;
        if (sftpHeight <= 70)
        {
            ToggleSftp();
            return;
        }
        if (terminalHeight < 180) return;
        _workspaceGrid.RowDefinitions[0].Height = new GridLength(terminalHeight);
        _workspaceGrid.RowDefinitions[2].Height = new GridLength(sftpHeight);
        _previousSftpHeight = sftpHeight;
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("新建文件夹", "文件夹名称");
        if (string.IsNullOrWhiteSpace(name) || _activeService?.SftpClient is null) return;
        try { await Task.Run(() => _activeService.SftpClient.CreateDirectory(CombinePath(_currentPath, name))); await RefreshRemoteFilesAsync(); }
        catch (Exception ex) { _transferStatus.Text = $"新建失败：{ex.Message}"; }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeService?.SftpClient is null) return;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, _windowHandle);
        var files = await picker.PickMultipleFilesAsync(); if (files.Count == 0) return;
        await _transferGate.WaitAsync();
        _transferCts = new CancellationTokenSource();
        _cancelTransferButton.Visibility = Visibility.Visible;
        try
        {
            foreach (var file in files)
            {
                var remotePath = CombinePath(_currentPath, file.Name);
                if (_activeService.SftpClient.Exists(remotePath) && !await ConfirmOverwriteAsync(file.Name)) continue;
                _transferStatus.Text = $"正在上传 {file.Name}";
                _transferProgress.Visibility = Visibility.Visible;
                using var input = await file.OpenStreamForReadAsync();
                await _activeService.SftpClient.UploadFileAsync(input, remotePath, _transferCts.Token);
            }
            _transferStatus.Text = "上传完成";
        }
        catch (OperationCanceledException) { _transferStatus.Text = "上传已取消"; }
        catch (Exception ex) { _transferStatus.Text = $"上传失败：{ex.Message}"; }
        finally
        {
            _transferProgress.Visibility = Visibility.Collapsed;
            _cancelTransferButton.Visibility = Visibility.Collapsed;
            _transferCts.Dispose(); _transferCts = null; _transferGate.Release();
        }
        await RefreshRemoteFilesAsync();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e) => await DownloadSelectedAsync();

    private async Task DownloadSelectedAsync()
    {
        if (SelectedRemoteItem is not { IsDirectory: false } item || _activeService?.SftpClient is null) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        var localPath = Path.Combine(folder.Path, item.Name);
        if (File.Exists(localPath) && !await ConfirmOverwriteAsync(item.Name)) return;
        await _transferGate.WaitAsync();
        _transferCts = new CancellationTokenSource();
        _cancelTransferButton.Visibility = Visibility.Visible;
        try
        {
            _transferStatus.Text = $"正在下载 {item.Name}";
            _transferProgress.Visibility = Visibility.Visible;
            var local = await folder.CreateFileAsync(item.Name, CreationCollisionOption.ReplaceExisting);
            using var output = File.Create(local.Path);
            await _activeService.SftpClient.DownloadFileAsync(item.FullPath, output, _transferCts.Token);
            _transferStatus.Text = "下载完成";
        }
        catch (OperationCanceledException) { _transferStatus.Text = "下载已取消"; }
        catch (Exception ex) { _transferStatus.Text = $"下载失败：{ex.Message}"; }
        finally
        {
            _transferProgress.Visibility = Visibility.Collapsed;
            _cancelTransferButton.Visibility = Visibility.Collapsed;
            _transferCts.Dispose(); _transferCts = null; _transferGate.Release();
        }
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    private async Task RenameSelectedAsync()
    {
        if (SelectedRemoteItem is not { } item || _activeService?.SftpClient is null) return;
        var name = await PromptTextAsync("重命名", "输入新名称");
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        try
        {
            await _activeService.SftpClient.RenameFileAsync(item.FullPath, CombinePath(_currentPath, name), CancellationToken.None);
            await RefreshRemoteFilesAsync();
        }
        catch (Exception ex) { _transferStatus.Text = $"重命名失败：{ex.Message}"; }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        if (SelectedRemoteItem is not { } item || _activeService?.SftpClient is null) return;
        var dialog = new ContentDialog { Title = "确认删除", Content = $"确定删除“{item.Name}”吗？仅允许删除空目录。", PrimaryButtonText = "删除", CloseButtonText = "取消", XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (item.IsDirectory) await Task.Run(() => _activeService.SftpClient.DeleteDirectory(item.FullPath));
            else await Task.Run(() => _activeService.SftpClient.DeleteFile(item.FullPath));
            await RefreshRemoteFilesAsync();
        }
        catch (Exception ex) { _transferStatus.Text = $"删除失败：{ex.Message}"; }
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var all = _history.AsEnumerable().Reverse().ToList();
        var search = new TextBox { PlaceholderText = "搜索本次会话命令" };
        var list = new ListView { ItemsSource = all, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
        search.TextChanged += (_, _) => list.ItemsSource = string.IsNullOrWhiteSpace(search.Text) ? all : all.Where(command => command.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(search); content.Children.Add(list);
        var dialog = new ContentDialog { Title = "本次会话历史", Content = content, PrimaryButtonText = "填入输入框", CloseButtonText = "关闭", XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is string command) _composer.Text = command;
    }

    private async Task<string> PromptTextAsync(string title, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder };
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "确定", CloseButtonText = "取消", XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : string.Empty;
    }

    private async Task<bool> ConfirmOverwriteAsync(string name)
    {
        var dialog = new ContentDialog { Title = "文件已存在", Content = $"“{name}”已存在，是否覆盖？", PrimaryButtonText = "覆盖", CloseButtonText = "取消", XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private string NormalizeRemotePath(string path)
    {
        path = path.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(path)) return _currentPath;
        if (!path.StartsWith('/')) path = CombinePath(_currentPath, path);
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    private static string CombinePath(string path, string name) => path == "/" ? "/" + name.TrimStart('/') : path.TrimEnd('/') + "/" + name.TrimStart('/');
    private static string FormatBytes(long length) => length switch { < 1024 => $"{length} B", < 1024 * 1024 => $"{length / 1024d:0.0} KB", < 1024L * 1024 * 1024 => $"{length / 1024d / 1024d:0.0} MB", _ => $"{length / 1024d / 1024d / 1024d:0.0} GB" };

    public async ValueTask DisposeAsync()
    {
        _metricsCts?.Cancel();
        _transferCts?.Cancel();
        if (_activeService is not null)
        {
            _activeService.OutputReceived -= Connection_OutputReceived;
            _activeService.Disconnected -= Connection_Disconnected;
        }
        if (_activeService is not null) await _activeService.DisposeAsync();
        _terminalView.Loaded -= TerminalView_Loaded;
        if (_terminalView.CoreWebView2 is not null) _terminalView.CoreWebView2.WebMessageReceived -= TerminalView_WebMessageReceived;
    }
}
