using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Session;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views;

/// <summary>
/// 一个会话标签页的可视外壳：终端与 SFTP 面板的布局、拆分条与折叠。
/// 连接本身归 <see cref="SessionConnection"/>；本控件只负责把它的事件编组回 UI 线程。
/// </summary>
public sealed class SessionWorkspace : UserControl, IShellSession, IAsyncDisposable
{
    private const string PanelBottomExpandPath = "M10.5 8.82585L11.3737 9.82437C11.5556 10.0322 11.8714 10.0532 12.0793 9.87141C12.2871 9.68956 12.3081 9.37368 12.1263 9.16586L10.3763 7.16586C10.2814 7.05736 10.1442 6.99512 10 6.99512C9.85583 6.99512 9.71866 7.05736 9.62372 7.16586L7.87372 9.16586C7.69188 9.37368 7.71294 9.68956 7.92075 9.87141C8.12857 10.0532 8.44445 10.0322 8.6263 9.82437L9.50001 8.82583L9.50001 12.5049C9.50001 12.781 9.72387 13.0049 10 13.0049C10.2762 13.0049 10.5 12.781 10.5 12.5049L10.5 8.82585ZM4 4C2.89543 4 2 4.89543 2 6V14C2 15.1046 2.89543 16 4 16H16C17.1046 16 18 15.1046 18 14V6C18 4.89543 17.1046 4 16 4H4ZM3 6C3 5.44772 3.44772 5 4 5H16C16.5523 5 17 5.44772 17 6V11H11.5V12H17V14C17 14.5523 16.5523 15 16 15H4C3.44772 15 3 14.5523 3 14V12H8.50003V11H3V6Z";

    private readonly ServerProfile _profile;
    private readonly ElementTheme _workspaceTheme;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly TerminalPane _terminalPane = new();
    private readonly Grid _workspaceGrid = new();
    private readonly Button _sftpRestoreButton = new();
    private readonly SessionConnection _connection;
    private readonly SftpWorkspaceView _sftpView;
    private readonly SftpWorkspace _sftpWorkspace;
    private bool _isSftpCollapsed;
    private double _previousSftpHeight = 260;

    public SessionWorkspace(
        ServerProfile profile,
        IntPtr windowHandle,
        Func<string, ISshConnection> connectionFactory,
        Func<HostFingerprintRequiredEventArgs, Task<bool>> fingerprintConfirmation,
        Func<Task<string?>> passwordProvider,
        ElementTheme workspaceTheme)
    {
        _profile = profile;
        _workspaceTheme = workspaceTheme == ElementTheme.Dark
            ? ElementTheme.Dark
            : ElementTheme.Light;
        RequestedTheme = _workspaceTheme;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _connection = new SessionConnection(
            profile,
            connectionFactory,
            passwordProvider,
            fingerprintConfirmation,
            work => _dispatcherQueue.TryEnqueue(() => work()),
            CancelSftpTransfer);
        _sftpView = new SftpWorkspaceView(windowHandle, _workspaceTheme);
        _sftpWorkspace = new SftpWorkspace(_connection.RemoteFiles, _sftpView);

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

    public ServerProfile Profile => _profile;
    public string DisplayTitle => _profile.Name;
    public bool IsConnected => _connection.IsConnected;
    public SessionConnectionState ConnectionState => _connection.State;
    public bool IsTransferActive => _sftpWorkspace.IsTransferActive;

    public event EventHandler<ServerMetrics?>? MetricsUpdated;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ConnectionFailed;

    public Task ConnectAsync() => _connection.ConnectAsync();

    public void SetActive(bool active) => _connection.SetActive(active);

    public void SetTerminalFontSize(double value) => _terminalPane.SetFontSize(value);

    private UIElement BuildLayout()
    {
        _workspaceGrid.Background = null;
        _workspaceGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(65, GridUnitType.Star),
            MinHeight = 180
        });
        _workspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        _workspaceGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(35, GridUnitType.Star),
            MinHeight = 120
        });
        _workspaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var terminalGrid = BuildTerminalGrid();
        _workspaceGrid.Children.Add(terminalGrid);

        var splitter = new Thumb
        {
            Background = ThemeBrush("SubtleStrokeBrush"),
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        splitter.DragDelta += Splitter_DragDelta;
        splitter.DoubleTapped += Splitter_DoubleTapped;
        Grid.SetRow(splitter, 1);
        _workspaceGrid.Children.Add(splitter);

        Grid.SetRow(_sftpView, 2);
        _workspaceGrid.Children.Add(_sftpView);

        var restoreRow = new Grid { Padding = new Thickness(0, 4, 0, 4) };
        _sftpRestoreButton.Content = CreateFluentPathIcon(PanelBottomExpandPath);
        _sftpRestoreButton.Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"];
        _sftpRestoreButton.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTipService.SetToolTip(_sftpRestoreButton, "展开 SFTP 文件管理器");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_sftpRestoreButton, "展开 SFTP 文件管理器");
        _sftpRestoreButton.Click += SftpRestoreButton_Click;
        _sftpRestoreButton.Visibility = Visibility.Collapsed;
        restoreRow.Children.Add(_sftpRestoreButton);
        Grid.SetRow(restoreRow, 3);
        _workspaceGrid.Children.Add(restoreRow);

        return _workspaceGrid;
    }

    private Grid BuildTerminalGrid()
    {
        var grid = new Grid { Padding = new Thickness(0, 0, 0, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(_terminalPane);
        return grid;
    }

    private Brush ThemeBrush(string key)
    {
        var themeKey = _workspaceTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary) &&
            dictionary is ResourceDictionary themeDictionary &&
            themeDictionary.ContainsKey(key))
        {
            return (Brush)themeDictionary[key];
        }
        return (Brush)Application.Current.Resources[key];
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
        await _sftpWorkspace.RefreshAsync();
        _terminalPane.FocusTerminal();
    }

    private void CancelSftpTransfer() => _sftpWorkspace.CancelTransfer();

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
        _isSftpCollapsed = !_isSftpCollapsed;
        if (_isSftpCollapsed)
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

        _sftpView.Visibility = _isSftpCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _sftpRestoreButton.Visibility = _isSftpCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Splitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isSftpCollapsed) return;
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

    private static PathIcon CreateFluentPathIcon(string pathData) =>
        (PathIcon)XamlReader.Load(
            $"<PathIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{pathData}\" Width=\"20\" Height=\"20\" />");

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
        _terminalPane.Dispose();
        _sftpWorkspace.Dispose();
    }
}
