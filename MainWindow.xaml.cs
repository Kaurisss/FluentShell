using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views;
using FluentShell.Views.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using WinRT.Interop;

namespace FluentShell;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly ShellCoordinator _shell;
    private readonly TransferCenter _transfers = new();
    private readonly TransferCenterView _transferView = new();
    private readonly Flyout _transferFlyout = new();
    private bool _transferFlyoutOpen;
    private readonly SessionTabStrip _sessionTabStrip = new();
    private readonly SessionHost _sessionHost;
    private readonly OverviewPage _overviewPage = new();
    private readonly ServerCatalogPage _serverCatalogPage;
    private readonly SettingsPage _settingsPage;
    private readonly ShellLayoutMode _layout = new();
    private Storyboard? _pageEntranceStoryboard;
    private bool _loaded;
    private bool _isSessionLayout;
    private bool _hasDisplayedPage;
    private string? _currentPage;
    private bool? _sidebarPreference;
    private readonly InfoBar _transferNotice = new() { Title = "传输完成", Severity = InfoBarSeverity.Success, IsClosable = true,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, MaxWidth = 440, Margin = new Thickness(16) };

    public MainWindow()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        _windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        ConfigureWindow();

        _sessionHost = new SessionHost(_sessionTabStrip);
        _shell = new ShellCoordinator(
            new LocalStore(),
            (profile, secretProvider, fingerprintConfirmation) => new SessionWorkspace(
                profile,
                _windowHandle,
                (secret, cancellationToken) => CreateConnectionAsync(profile, secret, cancellationToken),
                fingerprintConfirmation,
                secretProvider,
                _transfers),
            profile => ShellDialogService.PromptSecretAsync(Content.XamlRoot, profile),
            fingerprint => ShellDialogService.ConfirmFingerprintAsync(Content.XamlRoot, fingerprint));
        _serverCatalogPage = new ServerCatalogPage(
            _windowHandle,
            _shell.HasSavedCredential) { TestConnectionAsync = _shell.TestConnectionAsync };
        _settingsPage = new SettingsPage(_windowHandle);

        _transferView.SetCenter(_transfers);
        _transferView.CloseRequested += (_, _) => _transferFlyout.Hide();
        _transferFlyout.Content = _transferView;
        // FlyoutPresenter defaults to a narrower viewport than our task content.
        // Set its bounds explicitly and let only the inner task list scroll.
        _transferFlyout.FlyoutPresenterStyle = (Style)RootGrid.Resources["TransferFlyoutPresenterStyle"];
        _transferFlyout.Placement = FlyoutPlacementMode.RightEdgeAlignedBottom;
        _transferFlyout.Opened += (_, _) => _transferFlyoutOpen = true;
        _transferFlyout.Closed += (_, _) => _transferFlyoutOpen = false;
        _transfers.Changed += (_, _) =>
        {
            var count = _transfers.RunningCount;
            TransfersBadge.Value = count > 0 ? count : -1;
            TransfersBadge.Visibility = count > 0 || _transfers.FailedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TransfersNavItem,
                $"传输任务，{count} 项进行中，{_transfers.FailedCount} 项失败");
        };
        WireModules();
        Grid.SetRow(_transferNotice, 1);
        Canvas.SetZIndex(_transferNotice, 50);
        RootGrid.Children.Add(_transferNotice);
        _transfers.Completed += (_, task) =>
        {
            if (!_shell.Settings.Preferences.NotifyTransferComplete) return;
            _transferNotice.Message = $"{task.Direction} · {task.Title}";
            _transferNotice.IsOpen = true;
        };
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        Activated += (_, _) => _ = LoadAsync();
        Closed += (_, _) => _serverCatalogPage.CloseEditorWindows();
    }

    private void ConfigureWindow()
    {
        ApplyBackdrop("Mica");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FluentShell.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        _appWindow.Resize(new SizeInt32(1440, 900));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1700;
            presenter.PreferredMinimumHeight = 960;
        }
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(AppTitleBar);
    }

    private Task<ISshConnection?> CreateConnectionAsync(
        ServerProfile profile,
        string secret,
        CancellationToken cancellationToken) =>
        _shell.CreateConnectionAsync(profile, secret, cancellationToken);

    private void WireModules()
    {
        SessionTabHost.Content = _sessionTabStrip;
        _sessionHost.NewSessionRequested += (_, _) =>
        {
            _sessionHost.Select(null);
            ShowUnconnectedLayout("overview");
        };
        _sessionHost.SessionSelected += (_, session) => _shell.SelectSession(session);
        _sessionHost.SessionCloseRequested += async (_, session) =>
            await _shell.CloseSessionAsync(session, ConfirmCloseSessionAsync);
        _sessionHost.ContentChanged += (_, session) =>
            SessionContentPresenter.Content = session?.ContentElement;
        ConnectedSidebar.ReconnectRequested += ConnectedSidebar_ReconnectRequested;

        _overviewPage.ConnectRequested += async (_, profile) => await _shell.ConnectAsync(profile);
        _overviewPage.ConnectServerRequested += async (_, _) => await OpenServerPickerAsync();
        _overviewPage.AddServerRequested += async (_, _) =>
            await _serverCatalogPage.ShowAddWindowAsync(Content.XamlRoot);

        _serverCatalogPage.RefreshRequested += (_, _) => RenderServerCatalog();
        _serverCatalogPage.ConnectRequested += async (_, profile) => await _shell.ConnectAsync(profile);
        _serverCatalogPage.CopyRequested += async (_, profile) => await _shell.CopyProfileAsync(profile);
        _serverCatalogPage.DeleteRequested += async (_, profile) => await _shell.DeleteProfileAsync(profile);
        _serverCatalogPage.ProfileSaved += async (_, update) => await _shell.SaveProfileAsync(update);

        _settingsPage.SettingsChanged += async (_, update) =>
        {
            try
            {
                await _shell.UpdateSettingsAsync(update);
                ApplySettings(_shell.Settings);
            }
            catch (Exception)
            {
                DiagnosticLog.Record("SettingsSaveFailed");
                await ShellDialogService.ShowMessageAsync(Content.XamlRoot, "设置保存失败", "请检查数据目录权限或磁盘空间，然后重试。");
            }
        };
        _settingsPage.ClearLocalDataRequested += (_, _) => _shell.ClearLocalData();

        _shell.StateChanged += (_, _) => RenderState();
        _shell.ConnectionProgressChanged += (_, args) => SetConnectionProgress(args);
        _shell.ConnectionFailed += (_, args) => _dispatcherQueue.TryEnqueue(async () =>
            await ShellDialogService.ShowMessageAsync(
                Content.XamlRoot,
                $"无法连接到 {args.Profile.Name}",
                args.Message));
        _shell.SessionAdded += (_, session) =>
        {
            if (session is SessionWorkspace workspace) workspace.ShortcutRequested += Workspace_ShortcutRequested;
            _sessionHost.Add(session);
            ShowConnectedLayout();
        };
        _shell.SessionRemoved += (_, session) =>
        {
            if (session is SessionWorkspace workspace) workspace.ShortcutRequested -= Workspace_ShortcutRequested;
            _sessionHost.Remove(session);
        };
        _shell.SessionSelected += (_, session) =>
        {
            if (session is null)
            {
                ShowUnconnectedLayout("servers");
                return;
            }

            _sessionHost.Select(session);
            ShowConnectedLayout();
        };
        _shell.MetricsUpdated += (_, args) =>
        {
            if (ReferenceEquals(_shell.SelectedSession, args.Session))
                ConnectedSidebar.UpdateMetrics(
                    args.Session.Id,
                    args.Metrics,
                    !_layout.IsSidebarCollapsed);
        };
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await _shell.LoadAsync();
        ApplySettings(_shell.Settings);
        RootNavigationView.SelectedItem = OverviewNavItem;
        NavigateTo("overview");
    }

    private void RenderState()
    {
        _overviewPage.SetOverview(_shell.Profiles);
        _settingsPage.SetSettings(_shell.Settings, _shell.DataFolder);
        RenderServerCatalog();
        if (_shell.SelectedSession is { } session)
            ConnectedSidebar.UpdateSession(session.Id, session.Profile, session.ConnectionState);
    }

    private void RenderServerCatalog() => _serverCatalogPage.SetProfiles(_shell.Profiles);

    private void ApplySettings(AppSettings settings)
    {
        ApplyTheme(settings.Theme);
        ApplyBackdrop(settings.BackdropMaterial);
        _transfers.Limiter.SetLimit(settings.Preferences.MaxTransfers);
        if (!settings.Preferences.NotifyTransferComplete) _transferNotice.IsOpen = false;
        if (_sidebarPreference != settings.Preferences.SidebarOpen)
        {
            _sidebarPreference = settings.Preferences.SidebarOpen;
            _layout.SetDefaultPaneOpen(settings.Preferences.SidebarOpen);
            RootNavigationView.IsPaneOpen = settings.Preferences.SidebarOpen;
        }
        RootGrid.KeyboardAccelerators.Clear();
        foreach (var (action, key) in settings.Preferences.Shortcuts())
        {
            var accelerator = new KeyboardAccelerator { Key = Enum.Parse<VirtualKey>(key), Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
            accelerator.Invoked += async (_, args) =>
            {
                if (ShortcutKeyPicker.IsRecording) return;
                args.Handled = true;
                await ExecuteShortcutAsync(action);
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
    }

    private async void Workspace_ShortcutRequested(object? sender, string action) => await ExecuteShortcutAsync(action);

    private async Task ExecuteShortcutAsync(string action)
    {
        if (action == "new") { _sessionHost.Select(null); ShowUnconnectedLayout("overview"); }
        else if (action == "close" && _shell.SelectedSession is { } selected)
            await _shell.CloseSessionAsync(selected, ConfirmCloseSessionAsync);
        else if (action == "next")
        {
            var sessions = _shell.Sessions.ToList();
            if (sessions.Count > 0) _shell.SelectSession(sessions[(sessions.IndexOf(_shell.SelectedSession!) + 1) % sessions.Count]);
        }
        else if (_shell.SelectedSession is SessionWorkspace workspace) workspace.ExecuteShortcut(action);
    }

    private void ApplyTheme(string theme)
    {
        var requestedTheme = theme switch
        {
            "浅色" => ElementTheme.Light,
            "深色" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RootGrid.RequestedTheme = requestedTheme;
        RootNavigationView.RequestedTheme = requestedTheme;
        WindowChrome.ApplyTheme(_appWindow, RootNavigationView, _dispatcherQueue, theme);
    }

    private void ApplyBackdrop(string material) => SystemBackdrop = material == "亚克力"
        ? new DesktopAcrylicBackdrop()
        : new MicaBackdrop();

    private void RootNavigationView_Loaded(object sender, RoutedEventArgs e) =>
        UpdateResponsiveLayout(RootGrid.ActualWidth);

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layout.IsMeasured) UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double width)
    {
        var layout = _layout.Measure(width, RootNavigationView.IsPaneOpen);
        if (layout.PaneStateChanged)
        {
            using (_layout.BeginApplying())
            {
                RootNavigationView.PaneDisplayMode = layout.PaneDisplay == NavigationPaneDisplay.LeftMinimal
                    ? NavigationViewPaneDisplayMode.LeftMinimal
                    : NavigationViewPaneDisplayMode.Left;
                RootNavigationView.IsPaneOpen = layout.IsPaneOpen;
            }
        }

        var isNarrow = layout.IsNarrow;
        ApplyContentSpacing();
        SessionTabHost.Margin = new Thickness(
            RootNavigationView.CompactPaneLength,
            0,
            isNarrow ? 180 : 300,
            0);

    }

    /// <summary>
    /// 内容区留白随窗口宽度和"是否在会话里"两件事变化，两者都可能单独发生，
    /// 所以应用动作单独成一个方法，由各自的入口调用。
    /// </summary>
    private void ApplyContentSpacing()
    {
        var isNarrow = _layout.IsNarrow;
        var spacing = ShellLayoutMode.MeasureContentSpacing(isNarrow, _isSessionLayout);
        ContentHost.Padding = new Thickness(
            spacing.Horizontal,
            _isSessionLayout ? spacing.Horizontal : (isNarrow ? 16 : 24),
            _isSessionLayout ? spacing.Horizontal : 0,
            _isSessionLayout ? spacing.Bottom : 0);
        var pageSpacing = ShellLayoutMode.MeasureContentSpacing(isNarrow, false).Horizontal;
        _overviewPage.UpdateResponsiveLayout(pageSpacing);
        _settingsPage.UpdateResponsiveLayout(pageSpacing);
        _serverCatalogPage.UpdateResponsiveLayout(pageSpacing);
    }

    private void NavigateTo(string page)
    {
        var shouldAnimate = _hasDisplayedPage && !string.Equals(_currentPage, page, StringComparison.Ordinal);

        PageContentPresenter.Content = page switch
        {
            "servers" => _serverCatalogPage,
            "settings" => _settingsPage,
            _ => _overviewPage
        };
        _currentPage = page;
        _hasDisplayedPage = true;
        if (shouldAnimate)
            PlayPageEntranceAnimation();
    }

    private void PlayPageEntranceAnimation()
    {
        _pageEntranceStoryboard?.Stop();

        PageContentTransform.Y = 28;
        PageContentPresenter.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var offsetAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = easing
        };
        var opacityAnimation = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = easing
        };

        Storyboard.SetTarget(offsetAnimation, PageContentTransform);
        Storyboard.SetTargetProperty(offsetAnimation, "Y");
        Storyboard.SetTarget(opacityAnimation, PageContentPresenter);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        _pageEntranceStoryboard = new Storyboard();
        _pageEntranceStoryboard.Children.Add(offsetAnimation);
        _pageEntranceStoryboard.Children.Add(opacityAnimation);
        _pageEntranceStoryboard.Begin();
    }

    private void ShowConnectedLayout()
    {
        _isSessionLayout = true;
        ApplyContentSpacing();
        PageContentPresenter.Visibility = Visibility.Collapsed;
        SessionContentPresenter.Visibility = Visibility.Visible;
        SessionTabHost.Visibility = Visibility.Visible;
        OverviewNavItem.Visibility = Visibility.Collapsed;
        ServersNavItem.Visibility = Visibility.Collapsed;
        SettingsNavItem.Visibility = Visibility.Collapsed;
        TransfersNavItem.Visibility = Visibility.Visible;
        ConnectedSidebar.Visibility = Visibility.Visible;
        ConnectedSidebar.SetPaneOpen(RootNavigationView.IsPaneOpen);
    }

    private void ShowUnconnectedLayout(string page)
    {
        _isSessionLayout = false;
        ApplyContentSpacing();
        PageContentPresenter.Visibility = Visibility.Visible;
        SessionContentPresenter.Visibility = Visibility.Collapsed;
        SessionContentPresenter.Content = null;
        SessionTabHost.Visibility = _shell.SessionCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewNavItem.Visibility = Visibility.Visible;
        ServersNavItem.Visibility = Visibility.Visible;
        SettingsNavItem.Visibility = Visibility.Visible;
        _transferFlyout.Hide();
        TransfersNavItem.Visibility = Visibility.Collapsed;
        ConnectedSidebar.Visibility = Visibility.Collapsed;
        RootNavigationView.SelectedItem = page switch
        {
            "servers" => ServersNavItem,
            "settings" => SettingsNavItem,
            _ => OverviewNavItem
        };
        NavigateTo(page);
    }

    private void RootNavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!ReferenceEquals(args.InvokedItemContainer, TransfersNavItem)) return;
        if (_transferFlyoutOpen) { _transferFlyout.Hide(); return; }
        _transferView.Width = Math.Max(280, Math.Min(520, RootGrid.ActualWidth - 96));
        _transferView.Height = Math.Max(240, Math.Min(560, RootGrid.ActualHeight - 96));
        _transferView.RequestedTheme = RootGrid.ActualTheme;
        _transferFlyout.ShowAt(TransfersNavItem);
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_layout.IsNavigationLocked(_isSessionLayout) ||
            args.SelectedItemContainer?.Tag is not string page)
        {
            return;
        }
        NavigateTo(page);
    }

    private void RootNavigationView_PaneOpening(NavigationView sender, object args)
    {
        _layout.NotePaneOpened();
        UpdatePaneToggleButton(true);
        ConnectedSidebar.SetPaneOpen(true);
    }

    private void RootNavigationView_PaneClosing(
        NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        _layout.NotePaneClosed();
        UpdatePaneToggleButton(false);
        ConnectedSidebar.SetPaneOpen(false);
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e) =>
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;

    private void UpdatePaneToggleButton(bool isPaneOpen)
    {
        ExpandPaneIcon.Visibility = isPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        CollapsePaneIcon.Visibility = isPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        var accessibleName = isPaneOpen ? "收起侧栏" : "展开侧栏";
        ToolTipService.SetToolTip(PaneToggleButton, accessibleName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PaneToggleButton, accessibleName);
    }

    private async void ConnectedSidebar_ReconnectRequested(object? sender, EventArgs e) =>
        await _shell.ReconnectSelectedSessionAsync();

    private async Task OpenServerPickerAsync()
    {
        if (_shell.Profiles.Count == 0)
        {
            await ShellDialogService.ShowMessageAsync(Content.XamlRoot, "还没有服务器", "请先添加一台服务器，再打开新的连接标签页。");
            ShowUnconnectedLayout("servers");
            return;
        }

        var selected = await ShellDialogService.PickServerAsync(Content.XamlRoot, _shell.Profiles);
        if (selected is not null) await _shell.ConnectAsync(selected);
    }

    private Task<bool> ConfirmCloseSessionAsync(IShellSession session) =>
        ShellDialogService.ConfirmCloseSessionAsync(Content.XamlRoot, session.Profile.Name);

    private void SetConnectionProgress(ConnectionProgressChangedEventArgs args)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => SetConnectionProgress(args));
            return;
        }

        if (args.IsActive)
        {
            if (args.Message is not null)
                ConnectionDialogOverlay.UpdateMessage(args.Message);
            ConnectionDialogOverlay.Visibility = Visibility.Visible;
            ConnectionDialogOverlay.FocusCancelButton();
        }
        else
        {
            ConnectionDialogOverlay.Visibility = Visibility.Collapsed;
        }

        _serverCatalogPage.SetBusy(args.IsActive);
    }

    private void ConnectionDialogOverlay_CancelRequested(object? sender, EventArgs e) =>
        _shell.CancelConnection();
}
