using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public sealed partial class ServerCatalogPage : UserControl
{
    private readonly IntPtr _windowHandle;
    private readonly Func<ServerProfile, bool> _hasSavedCredential;
    public TestServerConnection? TestConnectionAsync { get; set; }
    private IReadOnlyList<ServerProfile> _profiles = [];
    private readonly Dictionary<Guid, ServerProfileWindow> _profileWindows = [];
    private bool _initialized;
    private readonly Style _profileItemStyle;

    public ServerCatalogPage(
        IntPtr windowHandle,
        Func<ServerProfile, bool> hasSavedCredential)
    {
        _windowHandle = windowHandle;
        _hasSavedCredential = hasSavedCredential;
        InitializeComponent();
        _profileItemStyle = ProfilesList.ItemContainerStyle;
        UpdateResponsiveLayout(30);
        _initialized = true;
    }

    public void UpdateResponsiveLayout(double horizontalSpacing)
    {
        Toolbar.Margin = new Thickness(0, 0, horizontalSpacing, 16);
        EmptyState.Margin = new Thickness(0, 0, horizontalSpacing, 0);
        var itemStyle = new Style(typeof(ListViewItem)) { BasedOn = _profileItemStyle };
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, horizontalSpacing, 0)));
        ProfilesList.ItemContainerStyle = itemStyle;
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler<ServerProfile>? ConnectRequested;
    public event EventHandler<ServerProfile>? CopyRequested;
    public event EventHandler<ServerProfile>? DeleteRequested;
    public event EventHandler<ServerProfileUpdate>? ProfileSaved;

    public void SetProfiles(IReadOnlyList<ServerProfile> profiles)
    {
        _profiles = profiles;
        ApplyFilter();
    }

    public void SetBusy(bool isBusy)
    {
        ProfilesList.IsEnabled = !isBusy;
        SearchBox.IsEnabled = !isBusy;
        SortComboBox.IsEnabled = !isBusy;
        RefreshButton.IsEnabled = !isBusy;
        AddButton.IsEnabled = !isBusy;
    }


    public Task ShowAddWindowAsync(XamlRoot xamlRoot) => ShowProfileWindowAsync(null, xamlRoot);

    public void CloseEditorWindows()
    {
        foreach (var window in _profileWindows.Values.ToArray()) window.Close();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => NotifyFilterChanged();
    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => NotifyFilterChanged();

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ServerProfile profile)
            ConnectRequested?.Invoke(this, profile);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ServerProfile profile)
            CopyRequested?.Invoke(this, profile);
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ServerProfile profile)
            await ShowProfileWindowAsync(profile, XamlRoot);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await ShowProfileWindowAsync(null, XamlRoot);

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ServerProfile profile) return;
        var dependentCount = _profiles.Count(candidate => candidate.JumpProfileId == profile.Id);
        var impact = dependentCount == 0
            ? string.Empty
            : $"另有 {dependentCount} 台已保存服务器将失去跳板配置，需重新选择跳板后才能连接。";
        var dialog = new ContentDialog
        {
            Title = "删除服务器",
            Content = $"确定删除“{profile.Name}”吗？不会影响远程主机。{impact}",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            DeleteRequested?.Invoke(this, profile);
    }

    private async Task ShowProfileWindowAsync(ServerProfile? editing, XamlRoot xamlRoot)
    {
        var key = editing?.Id ?? Guid.Empty;
        if (_profileWindows.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new ServerProfileWindow(editing, new ServerProfileWindowContext
        {
            OwnerXamlRoot = xamlRoot,
            OwnerWindowHandle = _windowHandle,
            HasSavedCredential = editing is not null && _hasSavedCredential(editing),
            ExistingProfiles = _profiles,
            TestConnectionAsync = TestConnectionAsync
        });
        _profileWindows.Add(key, window);
        window.Closed += (_, _) => _profileWindows.Remove(key);
        window.Activate();
        var result = await window.Completion;
        if (result is null) return;

        ProfileSaved?.Invoke(this, new ServerProfileUpdate(
            result.Profile,
            result.SaveCredential,
            result.CredentialIdentityChanged,
            result.OriginalUsername,
            result.ConnectAfterSave,
            result.EnteredSecret));
    }

    private void NotifyFilterChanged()
    {
        if (_initialized) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var sortOrder = SortComboBox.SelectedIndex == 1
            ? ServerSortOrder.RecentConnection
            : ServerSortOrder.Name;
        var filteredProfiles = ServerProfileQuery.Apply(_profiles, SearchBox.Text, sortOrder);
        ProfilesList.ItemsSource = filteredProfiles;
        ProfileCountText.Text = filteredProfiles.Count == _profiles.Count
            ? $"{_profiles.Count} 台"
            : $"显示 {filteredProfiles.Count} / {_profiles.Count} 台";
        var isEmpty = filteredProfiles.Count == 0;
        ProfilesList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateTitle.Text = _profiles.Count == 0 ? "还没有已保存的服务器" : "没有匹配的服务器";
        EmptyStateDescription.Text = _profiles.Count == 0
            ? "点击“添加服务器”开始。"
            : "试试其他关键词。";
    }
}
