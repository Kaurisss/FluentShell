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
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace FluentShell;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly ShellCoordinator _shell;
    private readonly SessionTabStrip _sessionTabStrip = new();
    private readonly SessionHost _sessionHost;
    private readonly OverviewPage _overviewPage = new();
    private readonly ServerCatalogPage _serverCatalogPage;
    private readonly SettingsPage _settingsPage;
    private readonly ShellLayoutMode _layout = new();
    private bool _loaded;
    private bool _isSessionLayout;

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
                secret => new SshConnectionService(profile, secret),
                fingerprintConfirmation,
                secretProvider,
                RootGrid.ActualTheme),
            profile => ShellDialogService.PromptSecretAsync(Content.XamlRoot, profile),
            fingerprint => ShellDialogService.ConfirmFingerprintAsync(Content.XamlRoot, fingerprint));
        _serverCatalogPage = new ServerCatalogPage(
            _windowHandle,
            _shell.HasSavedCredential);
        _settingsPage = new SettingsPage(_windowHandle);

        WireModules();
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        Activated += (_, _) => _ = LoadAsync();
    }

    private void ConfigureWindow()
    {
        ApplyBackdrop("Mica");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FluentShell.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        _appWindow.Resize(new SizeInt32(1440, 900));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 700;
        }
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(AppTitleBar);
    }

    private void WireModules()
    {
        SessionTabHost.Content = _sessionTabStrip;
        _sessionHost.NewSessionRequested += async (_, _) => await OpenServerPickerAsync();
        _sessionHost.SessionSelected += async (_, session) => await _shell.ConnectAsync(session.Profile);
        _sessionHost.SessionCloseRequested += async (_, session) =>
            await _shell.CloseSessionAsync(session, ConfirmCloseSessionAsync);
        _sessionHost.ContentChanged += (_, session) =>
            SessionContentPresenter.Content = session?.ContentElement;
        ConnectedSidebar.ReconnectRequested += ConnectedSidebar_ReconnectRequested;

        _overviewPage.ConnectRequested += async (_, profile) => await _shell.ConnectAsync(profile);
        _overviewPage.ConnectServerRequested += async (_, _) => await OpenServerPickerAsync();
        _overviewPage.AddServerRequested += async (_, _) =>
            await _serverCatalogPage.ShowAddDialogAsync(Content.XamlRoot);

        _serverCatalogPage.RefreshRequested += (_, _) => RenderServerCatalog();
        _serverCatalogPage.ConnectRequested += async (_, profile) => await _shell.ConnectAsync(profile);
        _serverCatalogPage.CopyRequested += async (_, profile) => await _shell.CopyProfileAsync(profile);
        _serverCatalogPage.DeleteRequested += async (_, profile) => await _shell.DeleteProfileAsync(profile);
        _serverCatalogPage.ProfileSaved += async (_, update) => await _shell.SaveProfileAsync(update);

        _settingsPage.SettingsChanged += async (_, update) =>
        {
            await _shell.UpdateSettingsAsync(update);
            ApplySettings(_shell.Settings);
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
            _sessionHost.Add(session);
            ShowConnectedLayout();
        };
        _shell.SessionRemoved += (_, session) => _sessionHost.Remove(session);
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
                    args.Session.Profile.Id,
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
            ConnectedSidebar.UpdateSession(session.Profile, session.ConnectionState);
    }

    private void RenderServerCatalog() => _serverCatalogPage.SetProfiles(_shell.Profiles);

    private void ApplySettings(AppSettings settings)
    {
        ApplyTheme(settings.Theme);
        ApplyBackdrop(settings.BackdropMaterial);
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
        _serverCatalogPage.UpdateResponsiveLayout(isNarrow);
    }

    /// <summary>
    /// 内容区留白随窗口宽度和"是否在会话里"两件事变化，两者都可能单独发生，
    /// 所以应用动作单独成一个方法，由各自的入口调用。
    /// </summary>
    private void ApplyContentSpacing()
    {
        var isNarrow = _layout.IsNarrow;
        var spacing = ShellLayoutMode.MeasureContentSpacing(isNarrow, _isSessionLayout);
        ContentHeader.Padding = new Thickness(
            spacing.Horizontal,
            isNarrow ? 16 : 24,
            spacing.Horizontal,
            isNarrow ? 12 : 18);
        ContentHost.Padding = new Thickness(spacing.Horizontal, 0, spacing.Horizontal, spacing.Bottom);
    }

    private void NavigateTo(string page)
    {
        PageContentPresenter.Content = page switch
        {
            "servers" => _serverCatalogPage,
            "settings" => _settingsPage,
            _ => _overviewPage
        };
        PageTitleText.Text = page switch
        {
            "servers" => "已保存的服务器",
            "settings" => "设置",
            _ => "概览"
        };
        PageSubtitleText.Text = page switch
        {
            "servers" => "添加、编辑和连接本机保存的服务器配置。",
            "settings" => "连接安全与界面偏好。",
            _ => "从最近或已保存的服务器开始 SSH 会话。"
        };
    }

    private void ShowConnectedLayout()
    {
        _isSessionLayout = true;
        ApplyContentSpacing();
        ContentHeader.Visibility = Visibility.Collapsed;
        PageContentPresenter.Visibility = Visibility.Collapsed;
        SessionContentPresenter.Visibility = Visibility.Visible;
        SessionTabHost.Visibility = Visibility.Visible;
        OverviewNavItem.Visibility = Visibility.Collapsed;
        ServersNavItem.Visibility = Visibility.Collapsed;
        SettingsNavItem.Visibility = Visibility.Collapsed;
        ConnectedSidebar.Visibility = Visibility.Visible;
        ConnectedSidebar.SetPaneOpen(RootNavigationView.IsPaneOpen);
    }

    private void ShowUnconnectedLayout(string page)
    {
        _isSessionLayout = false;
        ApplyContentSpacing();
        ContentHeader.Visibility = Visibility.Visible;
        PageContentPresenter.Visibility = Visibility.Visible;
        SessionContentPresenter.Visibility = Visibility.Collapsed;
        SessionContentPresenter.Content = null;
        SessionTabHost.Visibility = Visibility.Collapsed;
        OverviewNavItem.Visibility = Visibility.Visible;
        ServersNavItem.Visibility = Visibility.Visible;
        SettingsNavItem.Visibility = Visibility.Visible;
        ConnectedSidebar.Visibility = Visibility.Collapsed;
        RootNavigationView.SelectedItem = page switch
        {
            "servers" => ServersNavItem,
            "settings" => SettingsNavItem,
            _ => OverviewNavItem
        };
        NavigateTo(page);
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_layout.IsNavigationLockedBySessions(_shell.SessionCount) ||
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
