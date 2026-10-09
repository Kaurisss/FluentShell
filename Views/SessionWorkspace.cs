using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Session;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using FluentSymbol = FluentIcons.Common.Symbol;
using FluentSymbolIcon = FluentIcons.WinUI.SymbolIcon;

namespace FluentShell.Views;

/// <summary>
/// 一个会话标签页的可视外壳：终端与 SFTP 面板的布局、底边拖拽与折叠。
/// 连接本身归 <see cref="SessionConnection"/>；本控件只负责把它的事件编组回 UI 线程。
/// </summary>
public sealed class SessionWorkspace : UserControl, IShellSession, IShellSessionCloseGuard, IAsyncDisposable
{
    /// <summary>覆盖在 xterm 底部内边距上的透明拖拽区域高度。</summary>
    private const double TerminalBottomDragHeight = 10;
    private const double MinTerminalHeight = 180;
    private const double MinSftpHeight = 120;

    /// <summary>越过 SFTP 最小高度后还要再往下拖这么多，才认定用户是想折叠而不是手抖。</summary>
    private const double CollapseOvershoot = 60;

    private readonly ServerProfile _profile;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly TerminalPane _terminalPane = new();
    private readonly Grid _workspaceGrid = new();
    private readonly Button _sftpRestoreButton = new();
    private readonly SessionConnection _connection;
    private readonly SftpWorkspaceView _sftpView;
    private readonly SftpWorkspace _sftpWorkspace;
    private readonly WorkspaceSplitter _splitter = new();
    private bool _isSftpCollapsed;
    private double _previousTerminalHeight = 65;
    private double _previousSftpHeight = 35;
    private double _dragStartTerminalHeight;
    private double _dragStartSftpHeight;
    private double _dragOffset;

    public SessionWorkspace(
        ServerProfile profile,
        IntPtr windowHandle,
        Func<string, CancellationToken, Task<ISshConnection?>> connectionFactory,
        Func<HostFingerprintRequiredEventArgs, Task<bool>> fingerprintConfirmation,
        Func<Task<string?>> passwordProvider,
        TransferCenter? transfers = null)
    {
        _profile = profile;
        // Inherit the live root theme, including when a cached session is reattached.
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _connection = new SessionConnection(
            profile,
            connectionFactory,
            passwordProvider,
            fingerprintConfirmation,
            work => _dispatcherQueue.TryEnqueue(() => work()),
            CancelSftpTransfer);
        _sftpView = new SftpWorkspaceView(windowHandle);
        // 字节进度回调来自传输流的写入线程，必须编组回 UI 线程再进快照发布。
        // 传输走独立 SFTP 通道，浏览目录不必等传输结束。
        _sftpWorkspace = new SftpWorkspace(
            _connection.RemoteFiles,
            _sftpView,
            dispatchProgress: work => _dispatcherQueue.TryEnqueue(() => work()),
            transferFileService: _connection.TransferRemoteFiles,
            transfers: transfers,
            connectionLabel: $"{profile.Name} · 会话 {Id.ToString("N")[..6]}");

        _connection.Output += Connection_Output;
        _connection.StatusChanged += Connection_StatusChanged;
        _connection.ConnectionFailed += Connection_ConnectionFailed;
        _connection.MetricsUpdated += Connection_MetricsUpdated;
        _connection.Connected += Connection_Connected;

        _terminalPane.InputReceived += TerminalPane_InputReceived;
        _terminalPane.ResizeRequested += TerminalPane_ResizeRequested;
        _terminalPane.InitializationFailed += TerminalPane_InitializationFailed;

        Content = BuildLayout();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public ServerProfile Profile => _profile;
    public string DisplayTitle => _profile.Name;
    public object ContentElement => this;
    public bool IsConnected => _connection.IsConnected;
    public SessionConnectionState ConnectionState => _connection.State;
    public bool IsTransferActive => _sftpWorkspace.IsTransferActive;
    public bool TryCloseTextEditor() => _sftpView.TryCloseTextEditor();
    public bool TryPrepareClose() => TryCloseTextEditor();
    public bool IsTextEditorOpen => _sftpView.IsTextEditorOpen;

    public event EventHandler<ServerMetrics?>? MetricsUpdated;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ConnectionFailed;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _connection.ConnectAsync(cancellationToken);

    public void SetActive(bool active) => _connection.SetActive(active);

    public void SetTerminalFontSize(double value) => _terminalPane.SetFontSize(value);
    public void SetTerminalColors(TerminalColors colors) => _terminalPane.SetColors(colors);
    public event EventHandler<string>? ShortcutRequested
    {
        add => _terminalPane.ShortcutRequested += value;
        remove => _terminalPane.ShortcutRequested -= value;
    }

    public void SetPreferences(UserPreferences preferences, string downloadDirectory)
    {
        _terminalPane.SetPreferences(preferences);
        _connection.SetPreferences(preferences);
        _sftpWorkspace.SetPreferences(preferences, downloadDirectory);
        _sftpView.SetPreferences(preferences);
    }

    public void ExecuteShortcut(string action)
    {
        if (action == "search") _terminalPane.Search();
        if (action == "files")
        {
            ToggleSftp();
        }
    }

    private UIElement BuildLayout()
    {
        if (!_profile.SupportsTerminal) return _sftpView;
        _workspaceGrid.Background = null;
        _workspaceGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(_previousTerminalHeight, GridUnitType.Star),
            MinHeight = MinTerminalHeight
        });
        _workspaceGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(_previousSftpHeight, GridUnitType.Star),
            MinHeight = MinSftpHeight
        });
        _workspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var terminalGrid = BuildTerminalGrid();
        _workspaceGrid.Children.Add(terminalGrid);

        ToolTipService.SetToolTip(_splitter, "拖动终端底部调整高度，双击折叠 SFTP 文件管理器");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_splitter, "拖动终端底部调整终端与 SFTP 文件管理器的高度");
        _splitter.DragStarted += Splitter_DragStarted;
        _splitter.DragDelta += Splitter_DragDelta;
        _splitter.DoubleTapped += Splitter_DoubleTapped;
        Grid.SetRow(_sftpView, 1);
        _workspaceGrid.Children.Add(_sftpView);

        // 这一行是 Auto 高：留白挂在按钮上而不是行上，SFTP 展开时按钮隐藏，整行就真的塌成 0。
        var restoreRow = new Grid();
        _sftpRestoreButton.Margin = new Thickness(0, 4, 0, 4);
        _sftpRestoreButton.Content = new FluentSymbolIcon { Symbol = FluentSymbol.PanelBottomExpand };
        _sftpRestoreButton.Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"];
        _sftpRestoreButton.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTipService.SetToolTip(_sftpRestoreButton, "展开 SFTP 文件管理器");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_sftpRestoreButton, "展开 SFTP 文件管理器");
        _sftpRestoreButton.Click += SftpRestoreButton_Click;
        _sftpRestoreButton.Visibility = Visibility.Collapsed;
        restoreRow.Children.Add(_sftpRestoreButton);
        Grid.SetRow(restoreRow, 2);
        _workspaceGrid.Children.Add(restoreRow);

        return _workspaceGrid;
    }

    private Grid BuildTerminalGrid()
    {
        var grid = new Grid();
        // Match the native input/file-grid outline in DIPs; WinUI handles DPI scaling.
        var terminalFrame = (Border)XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' BorderThickness='{ThemeResource TextControlBorderThemeThickness}' BorderBrush='{ThemeResource SubtleStrokeBrush}' CornerRadius='{ThemeResource ControlCornerRadius}' />");
        terminalFrame.Child = _terminalPane;
        grid.Children.Add(terminalFrame);
        _splitter.Height = TerminalBottomDragHeight;
        _splitter.VerticalAlignment = VerticalAlignment.Bottom;
        Canvas.SetZIndex(_splitter, 1);
        grid.Children.Add(_splitter);
        return grid;
    }

    private void Connection_Output(object? sender, string text) =>
        _dispatcherQueue.TryEnqueue(() => _terminalPane.Write(text));

    private void Connection_StatusChanged(object? sender, string status) =>
        StatusChanged?.Invoke(this, status);

    private void Connection_ConnectionFailed(object? sender, string message) =>
        ConnectionFailed?.Invoke(this, message);

    private void Connection_MetricsUpdated(object? sender, ServerMetrics? metrics) =>
        MetricsUpdated?.Invoke(this, metrics);

    private async void Connection_Connected(object? sender, EventArgs e)
    {
        _sftpWorkspace.RefreshTransferCommands();
        await _sftpWorkspace.RefreshAsync();
        if (_profile.SupportsTerminal) _terminalPane.FocusTerminal();
    }

    private void CancelSftpTransfer() => _sftpWorkspace.ConnectionLost();

    private async void TerminalPane_InputReceived(object? sender, string data) =>
        await _connection.SendAsync(data);

    private async void TerminalPane_ResizeRequested(
        object? sender,
        TerminalResizeRequestedEventArgs e) =>
        await _connection.ResizeTerminalAsync(e.Columns, e.Rows);

    private void TerminalPane_InitializationFailed(object? sender, string message) =>
        _terminalPane.Write($"\r\n[终端初始化失败] {message}\r\n");

    private void SftpRestoreButton_Click(object sender, RoutedEventArgs e) => ToggleSftp();

    private void Splitter_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) =>
        ToggleSftp();

    private void ToggleSftp()
    {
        if (!_profile.SupportsTerminal) return;
        if (_isSftpCollapsed)
            ExpandSftp();
        else
            CollapseSftp(
                _workspaceGrid.RowDefinitions[0].ActualHeight,
                _workspaceGrid.RowDefinitions[1].ActualHeight);
    }

    /// <summary>
    /// 折叠 SFTP 面板。传入的两个高度是展开时要恢复的比例，
    /// 从拖拽折叠时传拖拽起点的高度，免得恢复出来只剩最小高度那么一条。
    /// </summary>
    private void CollapseSftp(double terminalHeight, double sftpHeight)
    {
        _isSftpCollapsed = true;
        _previousTerminalHeight = Math.Max(MinTerminalHeight, terminalHeight);
        _previousSftpHeight = Math.Max(MinSftpHeight, sftpHeight);
        _workspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        _workspaceGrid.RowDefinitions[1].MinHeight = 0;
        _workspaceGrid.RowDefinitions[1].Height = new GridLength(0);
        UpdateSftpVisibility();
        // 折叠后原先的焦点元素（拆分条或 SFTP 内部）都不可见了，把焦点还给终端。
        _terminalPane.FocusTerminal();
    }

    private void ExpandSftp()
    {
        _isSftpCollapsed = false;
        _workspaceGrid.RowDefinitions[0].Height = new GridLength(_previousTerminalHeight, GridUnitType.Star);
        _workspaceGrid.RowDefinitions[1].MinHeight = MinSftpHeight;
        _workspaceGrid.RowDefinitions[1].Height = new GridLength(_previousSftpHeight, GridUnitType.Star);
        UpdateSftpVisibility();
    }

    private void UpdateSftpVisibility()
    {
        var collapsed = _isSftpCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _sftpView.Visibility = collapsed;
        _splitter.Visibility = collapsed;
        _sftpRestoreButton.Visibility = _isSftpCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Splitter_DragStarted(object? sender, DragStartedEventArgs e)
    {
        _dragOffset = 0;
        _dragStartTerminalHeight = _workspaceGrid.RowDefinitions[0].ActualHeight;
        _dragStartSftpHeight = _workspaceGrid.RowDefinitions[1].ActualHeight;
    }

    private void Splitter_DragDelta(object? sender, DragDeltaEventArgs e)
    {
        if (_isSftpCollapsed) return;

        // 从拖拽起点累加位移，而不是叠加到 ActualHeight 上：指针事件可能比布局帧更密，
        // ActualHeight 会是上一帧的旧值，叠加就会丢掉位移、拖起来不跟手。
        _dragOffset += e.VerticalChange;

        if (_dragStartSftpHeight - _dragOffset < MinSftpHeight - CollapseOvershoot)
        {
            CollapseSftp(_dragStartTerminalHeight, _dragStartSftpHeight);
            return;
        }

        var total = _dragStartTerminalHeight + _dragStartSftpHeight;
        if (total < MinTerminalHeight + MinSftpHeight) return;

        // 到边界就钳住而不是不响应，否则拖过头之后要原路退回同样的距离才重新跟手。
        var terminalHeight = Math.Clamp(
            _dragStartTerminalHeight + _dragOffset,
            MinTerminalHeight,
            total - MinSftpHeight);

        // 用星号权重而不是绝对像素，窗口缩放时两块仍按拖出来的比例分配。
        _workspaceGrid.RowDefinitions[0].Height = new GridLength(terminalHeight, GridUnitType.Star);
        _workspaceGrid.RowDefinitions[1].Height = new GridLength(total - terminalHeight, GridUnitType.Star);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Output -= Connection_Output;
        _connection.StatusChanged -= Connection_StatusChanged;
        _connection.ConnectionFailed -= Connection_ConnectionFailed;
        _connection.MetricsUpdated -= Connection_MetricsUpdated;
        _connection.Connected -= Connection_Connected;
        await _connection.DisposeAsync();

        _terminalPane.InputReceived -= TerminalPane_InputReceived;
        _terminalPane.ResizeRequested -= TerminalPane_ResizeRequested;
        _terminalPane.InitializationFailed -= TerminalPane_InitializationFailed;
        _sftpRestoreButton.Click -= SftpRestoreButton_Click;
        _splitter.DragStarted -= Splitter_DragStarted;
        _splitter.DragDelta -= Splitter_DragDelta;
        _splitter.DoubleTapped -= Splitter_DoubleTapped;
        _terminalPane.Dispose();
        _sftpWorkspace.Dispose();
    }
}
