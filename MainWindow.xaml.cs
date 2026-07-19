using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentShell;

public sealed partial class MainWindow : Window
{
    private readonly ServerProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly CredentialService _credentialService = new();
    private readonly ObservableCollection<ServerProfile> _servers = [];
    private readonly Dictionary<Guid, SessionWorkspace> _sessions = [];
    private readonly List<SessionWorkspace> _sessionOrder = [];
    private readonly Dictionary<SessionWorkspace, ToggleButton> _sessionTabButtons = [];
    private readonly Dictionary<SessionWorkspace, Grid> _sessionTabContainers = [];
    private readonly HashSet<Guid> _connectingServers = [];
    private readonly Dictionary<Guid, string> _sessionSecrets = [];
    private readonly Dictionary<Guid, bool> _credentialPersistenceOverrides = [];
    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _sidebarCollapsed;
    private bool _loaded;
    private string _lastResult = "准备就绪";
    private AppSettings _settings = new();
    private bool _loadingSettings;
    private int _activeConnectionAttempts;
    private SessionWorkspace? _selectedSession;
    private bool _updatingSessionSelection;
    private bool _isNarrowLayout;
    private bool _paneWasOpenBeforeNarrow = true;

    public MainWindow()
    {
        _loadingSettings = true;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        ApplyBackdrop("Mica");
        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FluentShell.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        _appWindow.Resize(new SizeInt32(1440, 900));
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(AppTitleBar);
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        Activated += (_, _) => _ = LoadAsync();
        _loadingSettings = false;
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var profiles = await _profileStore.LoadAsync();
        _settings = await _settingsStore.LoadAsync();
        foreach (var profile in profiles) _servers.Add(profile);
        ServersListView.ItemsSource = _servers;
        UpdateOverview();
        DataLocationText.Text = _profileStore.GetDataFolder();
        ApplySettingsToControls();
        RootNavigationView.SelectedItem = OverviewNavItem;
        NavigateTo("overview");
        UpdateResponsiveLayout(RootGrid.ActualWidth);
    }

    private void ApplySettingsToControls()
    {
        _loadingSettings = true;
        ThemeComboBox.SelectedIndex = _settings.Theme switch { "浅色" => 1, "深色" => 2, _ => 0 };
        BackdropMaterialComboBox.SelectedIndex = _settings.BackdropMaterial == "亚克力" ? 1 : 0;
        TerminalFontSizeBox.Value = _settings.TerminalFontSize;
        DownloadDirectoryBox.Text = _settings.DownloadDirectory;
        RememberCredentialsToggle.IsOn = _settings.RememberCredentials;
        ApplyTheme(_settings.Theme);
        ApplyBackdrop(_settings.BackdropMaterial);
        _loadingSettings = false;
    }

    private void ApplyTheme(string theme)
    {
        var requestedTheme = theme switch { "浅色" => ElementTheme.Light, "深色" => ElementTheme.Dark, _ => ElementTheme.Default };
        RootGrid.RequestedTheme = requestedTheme;
        RootNavigationView.RequestedTheme = requestedTheme;
        _dispatcherQueue.TryEnqueue(() =>
        {
            ApplyTitleBarColors(theme);
            var pane = FindVisualChild<SplitView>(RootNavigationView);
            if (pane is null) return;
            pane.PaneBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        });
    }

    private void ApplyBackdrop(string material)
    {
        SystemBackdrop = material == "亚克力"
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop();
    }

    private void ApplyTitleBarColors(string theme)
    {
        var useDark = theme == "深色" || (theme == "系统" && RootGrid.ActualTheme == ElementTheme.Dark);
        var titleBar = _appWindow.TitleBar;
        var foreground = useDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var inactiveForeground = useDark
            ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
            : Windows.UI.Color.FromArgb(160, 0, 0, 0);
        var hoverBackground = useDark
            ? Windows.UI.Color.FromArgb(32, 255, 255, 255)
            : Windows.UI.Color.FromArgb(20, 0, 0, 0);
        var pressedBackground = useDark
            ? Windows.UI.Color.FromArgb(48, 255, 255, 255)
            : Windows.UI.Color.FromArgb(32, 0, 0, 0);

        titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        var isNarrow = width < 720;
        if (isNarrow != _isNarrowLayout)
        {
            if (isNarrow)
            {
                var wasOpen = RootNavigationView.IsPaneOpen;
                _isNarrowLayout = true;
                RootNavigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal;
                RootNavigationView.IsPaneOpen = false;
                _paneWasOpenBeforeNarrow = wasOpen;
            }
            else
            {
                _isNarrowLayout = false;
                RootNavigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                RootNavigationView.IsPaneOpen = _paneWasOpenBeforeNarrow;
            }
        }

        var contentPadding = isNarrow ? 16 : 30;
        ContentHeader.Padding = new Thickness(contentPadding, isNarrow ? 16 : 24, contentPadding, isNarrow ? 12 : 18);
        ContentHost.Padding = new Thickness(contentPadding, 0, contentPadding, isNarrow ? 16 : 28);
        SessionTabHost.Margin = isNarrow
            ? new Thickness(56, 0, 180, 0)
            : new Thickness(65, 0, 300, 0);
        ConnectionProgressText.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        ConnectionProgressPanel.Spacing = isNarrow ? 0 : 8;

        OverviewStatsGrid.ColumnSpacing = isNarrow ? 0 : 16;
        OverviewStatsGrid.RowSpacing = isNarrow ? 12 : 0;
        OverviewStatsGrid.RowDefinitions[1].Height = isNarrow ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(RecentServerOverviewCard, isNarrow ? 1 : 0);
        Grid.SetColumn(RecentServerOverviewCard, isNarrow ? 0 : 1);

        ServerListHeader.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        ServerToolbar.RowSpacing = isNarrow ? 10 : 0;
        ServerToolbar.RowDefinitions.Clear();
        if (isNarrow)
        {
            ServerToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ServerToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ServerToolbar.ColumnDefinitions[0].Width = new GridLength(136);
            ServerToolbar.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(ServerSearchBox, 0);
            Grid.SetColumn(ServerSearchBox, 0);
            Grid.SetColumnSpan(ServerSearchBox, 4);
            Grid.SetRow(ServerSortComboBox, 1);
            Grid.SetColumn(ServerSortComboBox, 0);
            Grid.SetRow(RefreshServersButton, 1);
            Grid.SetColumn(RefreshServersButton, 2);
            Grid.SetRow(AddServerPageButton, 1);
            Grid.SetColumn(AddServerPageButton, 3);
            AddServerPageButton.Content = "添加";
        }
        else
        {
            ServerToolbar.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            ServerToolbar.ColumnDefinitions[1].Width = new GridLength(150);
            Grid.SetRow(ServerSearchBox, 0);
            Grid.SetColumn(ServerSearchBox, 0);
            Grid.SetColumnSpan(ServerSearchBox, 1);
            Grid.SetRow(ServerSortComboBox, 0);
            Grid.SetColumn(ServerSortComboBox, 1);
            Grid.SetRow(RefreshServersButton, 0);
            Grid.SetColumn(RefreshServersButton, 2);
            Grid.SetRow(AddServerPageButton, 0);
            Grid.SetColumn(AddServerPageButton, 3);
            AddServerPageButton.Content = "添加服务器";
        }
    }

    private void NavigateTo(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ServersPage.Visibility = page == "servers" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = page switch { "servers" => "已保存的服务器", "settings" => "设置", _ => "概览" };
        PageSubtitleText.Text = page switch { "servers" => "添加、编辑和连接本机保存的服务器配置。", "settings" => "连接安全与界面偏好。", _ => "管理服务器连接，查看最近活动。" };
        QuickConnectButton.Visibility = Visibility.Collapsed;
        AddServerButton.Visibility = Visibility.Collapsed;
    }

    private void ShowConnectedLayout()
    {
        UnconnectedWorkspace.Visibility = Visibility.Collapsed;
        ConnectedWorkspace.Visibility = Visibility.Visible;
        ContentHeader.Visibility = Visibility.Collapsed;
        SessionTabHost.Visibility = Visibility.Visible;
        OverviewNavItem.Visibility = Visibility.Collapsed;
        ServersNavItem.Visibility = Visibility.Collapsed;
        SettingsNavItem.Visibility = Visibility.Collapsed;
        ConnectedSidebarPanel.Visibility = Visibility.Visible;
        ConnectedSidebarExpandedPanel.Visibility = RootNavigationView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        ConnectedSidebarCompactPanel.Visibility = RootNavigationView.IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        QuickConnectButton.Visibility = Visibility.Collapsed;
        AddServerButton.Visibility = Visibility.Collapsed;
        UpdateConnectedSidebar();
    }

    private void ShowUnconnectedLayout(string page = "servers")
    {
        ConnectedWorkspace.Visibility = Visibility.Collapsed;
        UnconnectedWorkspace.Visibility = Visibility.Visible;
        ContentHeader.Visibility = Visibility.Visible;
        SessionTabHost.Visibility = Visibility.Collapsed;
        SessionContentPresenter.Content = null;
        _selectedSession = null;
        OverviewNavItem.Visibility = Visibility.Visible;
        ServersNavItem.Visibility = Visibility.Visible;
        SettingsNavItem.Visibility = Visibility.Visible;
        ConnectedSidebarPanel.Visibility = Visibility.Collapsed;
        RootNavigationView.SelectedItem = page switch { "servers" => ServersNavItem, "settings" => SettingsNavItem, _ => OverviewNavItem };
        NavigateTo(page);
    }

    private void UpdateOverview()
    {
        SavedCountText.Text = _servers.Count.ToString();
        var recent = _servers.Where(server => server.LastConnectedAt is not null).OrderByDescending(server => server.LastConnectedAt).FirstOrDefault();
        RecentServerText.Text = recent?.Name ?? "暂无";
        RecentServerDetailText.Text = recent is null ? "连接成功后会显示在这里" : recent.LastConnectedLabel;
        LastResultText.Text = _lastResult;
        LastResultDetailText.Text = recent is null ? "还没有连接记录" : recent.Address;
    }

    private void UpdateConnectedSidebar()
    {
        var session = GetSelectedSession();
        if (session is null) return;
        var server = session.Profile;
        ConnectedServerNameText.Text = server.Name;
        ConnectedServerStatusText.Text = session.IsConnected ? "已连接" : "已断开";
        ConnectedAddressText.Text = server.Address;
        ConnectedUserText.Text = $"用户：{server.Username}";
    }

    private SessionWorkspace? GetSelectedSession() => _selectedSession;

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_sessions.Count > 0 || args.SelectedItemContainer?.Tag is not string page) return;
        NavigateTo(page);
    }

    private void RootNavigationView_PaneOpening(NavigationView sender, object args)
    {
        if (_isNarrowLayout) _paneWasOpenBeforeNarrow = true;
        else _sidebarCollapsed = false;
        UpdatePaneToggleButton(isPaneOpen: true);
        ConnectedSidebarExpandedPanel.Visibility = ConnectedSidebarPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ConnectedSidebarCompactPanel.Visibility = Visibility.Collapsed;
        foreach (var element in _metricElements.Values) element.Visibility = Visibility.Visible;
    }

    private void RootNavigationView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (_isNarrowLayout) _paneWasOpenBeforeNarrow = false;
        else _sidebarCollapsed = true;
        UpdatePaneToggleButton(isPaneOpen: false);
        ConnectedSidebarExpandedPanel.Visibility = Visibility.Collapsed;
        ConnectedSidebarCompactPanel.Visibility = ConnectedSidebarPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
    }

    private void UpdatePaneToggleButton(bool isPaneOpen)
    {
        ExpandPaneIcon.Visibility = isPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        CollapsePaneIcon.Visibility = isPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        var accessibleName = isPaneOpen ? "收起侧栏" : "展开侧栏";
        ToolTipService.SetToolTip(PaneToggleButton, accessibleName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PaneToggleButton, accessibleName);
    }

    private async void QuickConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions.Count == 0) ShowUnconnectedLayout("servers");
        else await ShowServerPickerAsync();
    }
    private void AddServerButton_Click(object sender, RoutedEventArgs e) => _ = ShowServerDialogAsync(null);
    private void RefreshServersButton_Click(object sender, RoutedEventArgs e) => RefreshServerList();

    private void RefreshServerList()
    {
        var filter = ServerSearchBox.Text.Trim();
        IEnumerable<ServerProfile> result = _servers;
        if (!string.IsNullOrWhiteSpace(filter)) result = result.Where(server => $"{server.Name} {server.Host} {server.Username}".Contains(filter, StringComparison.OrdinalIgnoreCase));
        result = ServerSortComboBox.SelectedIndex == 1
            ? result.OrderByDescending(server => server.LastConnectedAt).ThenBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            : result.OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase);
        ServersListView.ItemsSource = result.ToList();
        UpdateOverview();
    }

    private void ServerSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshServerList();
    private void ServerSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) RefreshServerList(); }

    private void ServersListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ServerProfile profile) _ = ConnectToServerAsync(profile);
    }

    private void ServerConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ServerProfile profile) _ = ConnectToServerAsync(profile);
    }

    private void SetConnectionProgress(bool isActive, string? message = null)
    {
        _activeConnectionAttempts = isActive
            ? _activeConnectionAttempts + 1
            : Math.Max(0, _activeConnectionAttempts - 1);
        var hasActiveConnection = _activeConnectionAttempts > 0;
        ConnectionProgressPanel.Visibility = hasActiveConnection ? Visibility.Visible : Visibility.Collapsed;
        ConnectionProgressRing.IsActive = hasActiveConnection;
        if (hasActiveConnection && !string.IsNullOrWhiteSpace(message)) ConnectionProgressText.Text = message;

        // Keep the current server list visible while preventing duplicate or conflicting actions.
        ServersListView.IsEnabled = !hasActiveConnection;
        ServerSearchBox.IsEnabled = !hasActiveConnection;
        ServerSortComboBox.IsEnabled = !hasActiveConnection;
        RefreshServersButton.IsEnabled = !hasActiveConnection;
        AddServerPageButton.IsEnabled = !hasActiveConnection;
    }

    private void ServerEditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ServerProfile profile) _ = ShowServerDialogAsync(profile);
    }

    private async void ServerCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerProfile source) return;
        var copy = new ServerProfile
        {
            Name = source.Name + " 副本",
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Authentication = source.Authentication,
            PrivateKeyPath = source.PrivateKeyPath,
            Notes = source.Notes,
            HostFingerprint = source.HostFingerprint,
            ShowHiddenFiles = source.ShowHiddenFiles
        };
        _servers.Add(copy);
        await _profileStore.SaveAsync(_servers);
        RefreshServerList();
    }

    private async void ServerDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerProfile profile) return;
        var dialog = new ContentDialog { Title = "删除服务器", Content = $"确定删除“{profile.Name}”吗？不会影响远程主机。", PrimaryButtonText = "删除", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _credentialService.Remove(profile);
        _servers.Remove(profile);
        await _profileStore.SaveAsync(_servers);
        RefreshServerList();
    }

    private async Task ShowServerDialogAsync(ServerProfile? editing)
    {
        var originalUsername = editing?.Username;
        var originalAuthentication = editing?.Authentication;
        var hasSavedCredential = editing is not null && _credentialService.TryGet(editing) is not null;
        var name = new TextBox { Header = "显示名称", Text = editing?.Name ?? string.Empty, PlaceholderText = "例如：生产服务器" };
        var host = new TextBox { Header = "主机地址", Text = editing?.Host ?? string.Empty, PlaceholderText = "example.com 或 IP 地址" };
        var port = new NumberBox { Header = "端口", Value = editing?.Port ?? 22, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var user = new TextBox { Header = "用户名", Text = editing?.Username ?? string.Empty };
        var auth = new ComboBox { Header = "认证方式", SelectedIndex = editing?.Authentication == AuthenticationMethod.PrivateKey ? 1 : 0 };
        auth.Items.Add(new ComboBoxItem { Content = "密码" }); auth.Items.Add(new ComboBoxItem { Content = "私钥" });
        var secret = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek };
        var rememberCredential = new CheckBox { Content = "保存凭据到 Windows 凭据管理器", IsChecked = hasSavedCredential || _settings.RememberCredentials };
        var credentialInfo = new TextBlock
        {
            Text = "凭据不会写入服务器配置文件。留空会保留已有凭据；取消勾选会删除这台服务器已保存的凭据。",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        var keyPath = new TextBox { Header = "私钥文件", Text = editing?.PrivateKeyPath ?? string.Empty, PlaceholderText = "选择 OpenSSH 私钥文件", IsReadOnly = true };
        var chooseKeyButton = new Button { Content = "选择文件", MinHeight = 40, VerticalAlignment = VerticalAlignment.Bottom };
        chooseKeyButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _windowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) keyPath.Text = file.Path;
        };
        var keyPickerRow = new Grid { ColumnSpacing = 8 };
        keyPickerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyPickerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyPickerRow.Children.Add(keyPath);
        Grid.SetColumn(chooseKeyButton, 1);
        keyPickerRow.Children.Add(chooseKeyButton);

        void UpdateAuthenticationFields()
        {
            var usesPrivateKey = auth.SelectedIndex == 1;
            var selectedAuthentication = usesPrivateKey ? AuthenticationMethod.PrivateKey : AuthenticationMethod.Password;
            var canPreserveSavedCredential = hasSavedCredential &&
                originalAuthentication == selectedAuthentication &&
                string.Equals(originalUsername, user.Text.Trim(), StringComparison.Ordinal);
            keyPickerRow.Visibility = usesPrivateKey ? Visibility.Visible : Visibility.Collapsed;
            secret.Header = usesPrivateKey ? "私钥口令（可选）" : "密码";
            secret.PlaceholderText = canPreserveSavedCredential
                ? "已保存；留空保持不变"
                : usesPrivateKey ? "私钥没有口令时可留空" : "输入登录密码";
        }

        auth.SelectionChanged += (_, _) => UpdateAuthenticationFields();
        user.TextChanged += (_, _) => UpdateAuthenticationFields();
        UpdateAuthenticationFields();
        var notes = new TextBox { Header = "备注", Text = editing?.Notes ?? string.Empty, PlaceholderText = "可选", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var form = new StackPanel { Spacing = 12, MaxWidth = 560 };
        foreach (var child in new UIElement[] { name, host, port, user, auth, keyPickerRow, secret, rememberCredential, credentialInfo, notes }) form.Children.Add(child);
        var dialog = new ContentDialog { Title = editing is null ? "添加服务器" : "编辑服务器", Content = new ScrollViewer { Content = form, MaxHeight = 620 }, PrimaryButtonText = editing is null ? "保存" : "保存修改", SecondaryButtonText = "保存并连接", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text))
        {
            await ShowMessageAsync("信息不完整", "显示名称、主机地址和用户名不能为空。");
            return;
        }
        if (auth.SelectedIndex == 1 && string.IsNullOrWhiteSpace(keyPath.Text))
        {
            await ShowMessageAsync("请选择私钥", "私钥认证需要选择一个本机私钥文件。");
            return;
        }
        var enteredSecret = secret.Password;
        var shouldSaveCredential = rememberCredential.IsChecked == true;
        var newUsername = user.Text.Trim();
        var selectedAuthentication = auth.SelectedIndex == 1 ? AuthenticationMethod.PrivateKey : AuthenticationMethod.Password;
        if (editing is not null &&
            (!string.Equals(originalUsername, newUsername, StringComparison.Ordinal) || originalAuthentication != selectedAuthentication))
        {
            _credentialService.Remove(editing);
        }
        var profile = editing ?? new ServerProfile();
        profile.Name = name.Text.Trim(); profile.Host = host.Text.Trim(); profile.Port = (int)(port.Value is double value && !double.IsNaN(value) ? value : 22); profile.Username = newUsername;
        profile.Authentication = selectedAuthentication;
        profile.PrivateKeyPath = keyPath.Text.Trim(); profile.Notes = notes.Text.Trim();
        if (editing is null) _servers.Add(profile);
        if (shouldSaveCredential)
        {
            if (!string.IsNullOrEmpty(enteredSecret)) _credentialService.Save(profile, enteredSecret);
        }
        else
        {
            _credentialService.Remove(profile);
        }
        await _profileStore.SaveAsync(_servers);
        RefreshServerList(); UpdateOverview();
        if (result == ContentDialogResult.Secondary)
        {
            _credentialPersistenceOverrides[profile.Id] = shouldSaveCredential;
            if (!string.IsNullOrEmpty(enteredSecret)) _sessionSecrets[profile.Id] = enteredSecret;
            _ = ConnectToServerAsync(profile);
        }
    }

    private async Task ConnectToServerAsync(ServerProfile profile)
    {
        if (_sessions.TryGetValue(profile.Id, out var existing))
        {
            SelectSession(existing);
            ShowConnectedLayout();
            return;
        }

        if (!_connectingServers.Add(profile.Id)) return;

        var workspace = new SessionWorkspace(profile, _windowHandle, ConfirmFingerprintAsync, () => PromptSecretAsync(profile), RootGrid.ActualTheme);
        workspace.SetTerminalFontSize(_settings.TerminalFontSize);
        workspace.StatusChanged += Session_StatusChanged;
        workspace.MetricsUpdated += Session_MetricsUpdated;
        SetConnectionProgress(true, $"正在连接 {profile.Name}…");
        try { await workspace.ConnectAsync(); }
        finally
        {
            _connectingServers.Remove(profile.Id);
            SetConnectionProgress(false);
        }
        if (!workspace.IsConnected)
        {
            _sessionSecrets.Remove(profile.Id);
            _credentialPersistenceOverrides.Remove(profile.Id);
            workspace.StatusChanged -= Session_StatusChanged;
            workspace.MetricsUpdated -= Session_MetricsUpdated;
            await workspace.DisposeAsync();
            return;
        }

        _sessions[profile.Id] = workspace;
        AddSessionTab(workspace);
        SelectSession(workspace);
        ShowConnectedLayout();
        profile.LastConnectedAt = DateTimeOffset.Now;
        var shouldPersistCredential = _credentialPersistenceOverrides.Remove(profile.Id, out var persistenceOverride)
            ? persistenceOverride
            : _settings.RememberCredentials;
        if (shouldPersistCredential && _sessionSecrets.TryGetValue(profile.Id, out var secret)) _credentialService.Save(profile, secret);
        else if (!shouldPersistCredential) _credentialService.Remove(profile);
        _sessionSecrets.Remove(profile.Id);
        await _profileStore.SaveAsync(_servers);
        UpdateOverview();
    }

    private void Session_StatusChanged(object? sender, string e)
    {
        _lastResult = e;
        UpdateConnectedSidebar();
        UpdateOverview();
    }

    private void Session_MetricsUpdated(object? sender, ServerMetrics? e)
    {
        if (!ReferenceEquals(sender, GetSelectedSession()) || e is null) return;
        BuildMetric("CPU", e.CpuPercent, true);
        BuildMetric("内存", e.MemoryPercent, true);
        BuildMetric("Swap", e.SwapPercent, true);
        CompactCpuText.Text = $"{e.CpuPercent:0}%";
        CompactMemoryText.Text = $"{e.MemoryPercent:0}%";
        CompactSwapText.Text = $"{e.SwapPercent:0}%";
        UpdateCompactMetric("CPU", e.CpuPercent);
        UpdateCompactMetric("内存", e.MemoryPercent);
        UpdateCompactMetric("Swap", e.SwapPercent);
        if (!_sidebarCollapsed)
        {
            BuildMetricText("负载", e.LoadAverage);
            BuildMetricText("系统", e.OperatingSystem);
            BuildMetricText("主机名", e.Hostname);
            BuildMetricText("运行时间", e.Uptime);
        }
    }

    private readonly Dictionary<string, FrameworkElement> _metricElements = [];
    private readonly Dictionary<string, double> _compactMetricValues = [];

    private void CompactMetricsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var metric in _compactMetricValues) UpdateCompactMetric(metric.Key, metric.Value);
    }

    private void UpdateCompactMetric(string label, double value)
    {
        _compactMetricValues[label] = Math.Clamp(value, 0, 100);
        var track = label switch
        {
            "CPU" => CompactCpuTrack,
            "内存" => CompactMemoryTrack,
            "Swap" => CompactSwapTrack,
            _ => null
        };
        var fill = label switch
        {
            "CPU" => CompactCpuFill,
            "内存" => CompactMemoryFill,
            "Swap" => CompactSwapFill,
            _ => null
        };
        if (track is not null && fill is not null)
        {
            fill.Width = track.ActualWidth * _compactMetricValues[label] / 100d;
            fill.Height = track.ActualHeight;
        }
    }

    private void BuildMetric(string label, double value, bool progress)
    {
        if (!_metricElements.TryGetValue(label, out var element))
        {
            var stack = new StackPanel { Spacing = 5 };
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            var valueText = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"] }; Grid.SetColumn(valueText, 1); row.Children.Add(valueText);
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Margin = new Thickness(0, 2, 0, 0) }; stack.Children.Add(row); stack.Children.Add(bar);
            element = stack; _metricElements[label] = stack; MetricsPanel.Children.Add(stack);
        }
        var children = ((StackPanel)element).Children; ((TextBlock)((Grid)children[0]).Children[1]).Text = $"{value:0}%"; ((ProgressBar)children[1]).Value = value;
    }

    private void BuildMetricText(string label, string value)
    {
        if (!_metricElements.TryGetValue(label, out var element))
        {
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"] });
            var valueText = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(valueText, 1); row.Children.Add(valueText);
            element = row; _metricElements[label] = row; MetricsPanel.Children.Add(row);
        }
        ((TextBlock)((Grid)element).Children[1]).Text = value;
    }

    private async Task<string?> PromptSecretAsync(ServerProfile profile)
    {
        if (_sessionSecrets.TryGetValue(profile.Id, out var provided)) return provided;
        if (_credentialService.TryGet(profile) is string saved)
        {
            _sessionSecrets[profile.Id] = saved;
            return saved;
        }
        var box = new PasswordBox { PlaceholderText = profile.Authentication == AuthenticationMethod.Password ? "输入密码" : "输入私钥口令（没有则留空）", PasswordRevealMode = PasswordRevealMode.Hidden };
        var dialog = new ContentDialog { Title = $"连接 {profile.Name}", Content = box, PrimaryButtonText = "连接", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        _sessionSecrets[profile.Id] = box.Password;
        return box.Password;
    }

    private async Task<bool> ConfirmFingerprintAsync(HostFingerprintRequiredEventArgs fingerprint)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "这是此服务器第一次连接。请确认主机指纹与你信任的来源一致。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"算法：{fingerprint.KeyType}\n指纹：{fingerprint.Fingerprint}", FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = "确认服务器指纹", Content = body, PrimaryButtonText = "信任并连接", CloseButtonText = "拒绝", XamlRoot = Content.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "知道了", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private void AddSessionTab(SessionWorkspace session)
    {
        var tabContainer = new Grid
        {
            Height = 40,
            MinWidth = 128,
            MaxWidth = 240
        };

        var tabButton = new ToggleButton
        {
            Tag = session,
            Content = new TextBlock
            {
                Text = session.DisplayTitle,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 168
            },
            Style = (Style)Application.Current.Resources["TitleBarSessionTabStyle"],
            Padding = new Thickness(12, 0, 40, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTipService.SetToolTip(tabButton, session.DisplayTitle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tabButton, $"切换到 {session.DisplayTitle} 会话");
        tabButton.Checked += SessionTabButton_Checked;
        tabContainer.Children.Add(tabButton);

        var closeButton = new Button
        {
            Tag = session,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 16 },
            Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        ToolTipService.SetToolTip(closeButton, "关闭会话");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(closeButton, $"关闭 {session.DisplayTitle} 会话");
        closeButton.Click += SessionTabCloseButton_Click;
        tabContainer.Children.Add(closeButton);

        _sessionOrder.Add(session);
        _sessionTabButtons[session] = tabButton;
        _sessionTabContainers[session] = tabContainer;
        SessionTabStrip.Children.Insert(Math.Max(0, SessionTabStrip.Children.Count - 1), tabContainer);
    }

    private void SessionTabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingSessionSelection || (sender as ToggleButton)?.Tag is not SessionWorkspace session) return;
        SelectSession(session);
    }

    private void SelectSession(SessionWorkspace session)
    {
        if (!_sessions.ContainsKey(session.Profile.Id)) return;
        _selectedSession = session;
        _updatingSessionSelection = true;
        foreach (var (candidate, tabButton) in _sessionTabButtons) tabButton.IsChecked = ReferenceEquals(candidate, session);
        _updatingSessionSelection = false;
        foreach (var candidate in _sessions.Values) candidate.SetActive(ReferenceEquals(candidate, session));
        SessionContentPresenter.Content = session;
        session.Profile.LastConnectedAt ??= DateTimeOffset.Now;
        UpdateConnectedSidebar();
    }

    private async void NewSessionButton_Click(object sender, RoutedEventArgs e) => await ShowServerPickerAsync();

    private async Task ShowServerPickerAsync()
    {
        if (_servers.Count == 0)
        {
            await ShowMessageAsync("还没有服务器", "请先添加一台服务器，再打开新的连接标签页。");
            return;
        }
        var list = new ListView { ItemsSource = _servers, DisplayMemberPath = nameof(ServerProfile.Name), SelectionMode = ListViewSelectionMode.Single, MinWidth = 380, MaxHeight = 420 };
        var dialog = new ContentDialog { Title = "打开服务器", Content = list, PrimaryButtonText = "连接", SecondaryButtonText = "添加服务器", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && list.SelectedItem is ServerProfile selected) await ConnectToServerAsync(selected);
        else if (result == ContentDialogResult.Secondary) await ShowServerDialogAsync(null);
    }

    private async void SessionTabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionWorkspace session) return;
        if (session.IsTransferActive)
        {
            var confirm = new ContentDialog { Title = "文件正在传输", Content = "关闭标签页会取消当前文件传输，是否继续？", PrimaryButtonText = "关闭标签页", CloseButtonText = "继续传输", XamlRoot = Content.XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var removedIndex = _sessionOrder.IndexOf(session);
        var wasSelected = ReferenceEquals(_selectedSession, session);
        _sessions.Remove(session.Profile.Id);
        _sessionOrder.Remove(session);
        _sessionTabButtons.Remove(session);
        if (_sessionTabContainers.Remove(session, out var tabContainer)) SessionTabStrip.Children.Remove(tabContainer);
        await session.DisposeAsync();
        if (_sessions.Count == 0)
        {
            ShowUnconnectedLayout("servers");
        }
        else
        {
            var nextSession = wasSelected
                ? _sessionOrder[Math.Min(Math.Max(removedIndex, 0), _sessionOrder.Count - 1)]
                : _selectedSession ?? _sessionOrder[0];
            SelectSession(nextSession);
        }
    }

    private async void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.Theme = ThemeComboBox.SelectedIndex switch { 1 => "浅色", 2 => "深色", _ => "系统" };
        ApplyTheme(_settings.Theme);
        await _settingsStore.SaveAsync(_settings);
    }

    private async void BackdropMaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.BackdropMaterial = BackdropMaterialComboBox.SelectedIndex == 1 ? "亚克力" : "Mica";
        ApplyBackdrop(_settings.BackdropMaterial);
        await _settingsStore.SaveAsync(_settings);
    }

    private async void TerminalFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings || double.IsNaN(sender.Value)) return;
        _settings.TerminalFontSize = sender.Value;
        foreach (var session in _sessions.Values) session.SetTerminalFontSize(sender.Value);
        await _settingsStore.SaveAsync(_settings);
    }

    private async void RememberCredentialsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.RememberCredentials = RememberCredentialsToggle.IsOn;
        if (!_settings.RememberCredentials) _credentialService.ClearAll();
        await _settingsStore.SaveAsync(_settings);
    }

    private async void ChooseDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, _windowHandle); var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) { DownloadDirectoryBox.Text = folder.Path; _settings.DownloadDirectory = folder.Path; await _settingsStore.SaveAsync(_settings); }
    }

    private async void ClearLocalDataButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { Title = "清除本地数据", Content = "这会删除所有已保存的服务器配置和已记录的主机指纹，远程服务器不会受到影响。", PrimaryButtonText = "清除", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _profileStore.ClearLocalData(); _credentialService.ClearAll(); _servers.Clear(); RefreshServerList(); UpdateOverview();
    }
}
