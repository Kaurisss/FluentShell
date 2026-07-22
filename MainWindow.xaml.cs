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
    private readonly SessionHost _sessionHost = new();
    private readonly OverviewPage _overviewPage = new();
    private readonly ServerCatalogPage _serverCatalogPage;
    private readonly SettingsPage _settingsPage;
    private bool _loaded;
    private bool? _isNarrowLayout;
    private bool _isApplyingResponsivePaneState;
    private bool _paneWasOpenBeforeNarrow = true;
    private bool _sidebarCollapsed;

    public MainWindow()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        _windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        ConfigureWindow();

        var credentialService = new CredentialService();
        _shell = new ShellCoordinator(
            new SettingsStore(),
            credentialService,
            new ServerCatalog(new ServerProfileStore(), credentialService),
            (profile, secretProvider, fingerprintConfirmation) => new SessionWorkspace(
                profile,
                _windowHandle,
                fingerprintConfirmation,
                secretProvider,
                RootGrid.ActualTheme),
            profile => ShellDialogService.PromptSecretAsync(Content.XamlRoot, profile),
            fingerprint => ShellDialogService.ConfirmFingerprintAsync(Content.XamlRoot, fingerprint));
        _serverCatalogPage = new ServerCatalogPage(
            _windowHandle,
            _shell.HasSavedCredential,
            () => _shell.Settings.RememberCredentials);
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
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(AppTitleBar);
    }

    private void WireModules()
    {
        SessionTabHost.Content = _sessionHost.TabStrip;
        _sessionHost.NewSessionRequested += async (_, _) => await OpenServerPickerAsync();
        _sessionHost.SessionSelected += async (_, session) => await _shell.ConnectAsync(session.Profile);
        _sessionHost.SessionCloseRequested += async (_, session) =>
            await _shell.CloseSessionAsync(session, ConfirmCloseSessionAsync);
        _sessionHost.ContentChanged += (_, session) => SessionContentPresenter.Content = session;

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
        _shell.SessionAdded += (_, session) =>
        {
            _sessionHost.Add((SessionWorkspace)session);
            ShowConnectedLayout();
        };
        _shell.SessionRemoved += (_, session) => _sessionHost.Remove((SessionWorkspace)session);
        _shell.SessionSelected += (_, session) =>
        {
            if (session is SessionWorkspace workspace)
            {
                _sessionHost.Select(workspace);
                ShowConnectedLayout();
            }
            else
            {
                ShowUnconnectedLayout("servers");
            }
        };
        _shell.MetricsUpdated += (_, args) =>
        {
            if (ReferenceEquals(_shell.SelectedSession, args.Session))
                ConnectedSidebar.UpdateMetrics(args.Metrics, !_sidebarCollapsed);
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
        _overviewPage.SetOverview(_shell.Profiles, _shell.LastResult);
        _settingsPage.SetSettings(_shell.Settings, _shell.DataFolder);
        RenderServerCatalog();
        if (_shell.SelectedSession is SessionWorkspace workspace)
            ConnectedSidebar.UpdateSession(workspace.Profile, workspace.IsConnected);
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
        if (_isNarrowLayout is not null) UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double width)
    {
        var isNarrow = width < 720;
        if (isNarrow != _isNarrowLayout)
        {
            _isApplyingResponsivePaneState = true;
            try
            {
                if (isNarrow)
                {
                    if (_isNarrowLayout is not null)
                        _paneWasOpenBeforeNarrow = RootNavigationView.IsPaneOpen;
                    _isNarrowLayout = true;
                    RootNavigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal;
                    RootNavigationView.IsPaneOpen = false;
                }
                else
                {
                    RootNavigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                    _isNarrowLayout = false;
                    RootNavigationView.IsPaneOpen = _paneWasOpenBeforeNarrow;
                }
            }
            finally
            {
                _isApplyingResponsivePaneState = false;
            }
        }

        var padding = isNarrow ? 16 : 30;
        ContentHeader.Padding = new Thickness(padding, isNarrow ? 16 : 24, padding, isNarrow ? 12 : 18);
        ContentHost.Padding = new Thickness(padding, 0, padding, isNarrow ? 16 : 28);
        SessionTabHost.Margin = isNarrow ? new Thickness(56, 0, 180, 0) : new Thickness(65, 0, 300, 0);
        ConnectionProgressText.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        ConnectionProgressPanel.Spacing = isNarrow ? 0 : 8;
        _overviewPage.UpdateResponsiveLayout(isNarrow);
        _serverCatalogPage.UpdateResponsiveLayout(isNarrow);
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
            _ => "管理服务器连接，查看最近活动。"
        };
    }

    private void ShowConnectedLayout()
    {
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
        if (_shell.SessionCount > 0 || args.SelectedItemContainer?.Tag is not string page) return;
        NavigateTo(page);
    }

    private void RootNavigationView_PaneOpening(NavigationView sender, object args)
    {
        if (!_isApplyingResponsivePaneState && _isNarrowLayout == false)
        {
            _paneWasOpenBeforeNarrow = true;
            _sidebarCollapsed = false;
        }
        UpdatePaneToggleButton(true);
        ConnectedSidebar.SetPaneOpen(true);
    }

    private void RootNavigationView_PaneClosing(
        NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (!_isApplyingResponsivePaneState && _isNarrowLayout == false)
        {
            _paneWasOpenBeforeNarrow = false;
            _sidebarCollapsed = true;
        }
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

    private async void QuickConnectButton_Click(object sender, RoutedEventArgs e) => await OpenServerPickerAsync();
    private async void AddServerButton_Click(object sender, RoutedEventArgs e) => await _serverCatalogPage.ShowAddDialogAsync();

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
        ConnectionProgressPanel.Visibility = args.IsActive ? Visibility.Visible : Visibility.Collapsed;
        ConnectionProgressRing.IsActive = args.IsActive;
        if (args.Message is not null) ConnectionProgressText.Text = args.Message;
        _serverCatalogPage.SetBusy(args.IsActive);
    }
}