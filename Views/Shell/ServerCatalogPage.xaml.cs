using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public sealed partial class ServerCatalogPage : UserControl
{
    private readonly IntPtr _windowHandle;
    private readonly Func<ServerProfile, bool> _hasSavedCredential;
    private IReadOnlyList<ServerProfile> _profiles = [];
    private bool _initialized;

    public ServerCatalogPage(
        IntPtr windowHandle,
        Func<ServerProfile, bool> hasSavedCredential)
    {
        _windowHandle = windowHandle;
        _hasSavedCredential = hasSavedCredential;
        InitializeComponent();
        _initialized = true;
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


    public Task ShowAddDialogAsync(XamlRoot xamlRoot) => ShowProfileDialogAsync(null, xamlRoot);

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
            await ShowProfileDialogAsync(profile, XamlRoot);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await ShowProfileDialogAsync(null, XamlRoot);

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

    private async Task ShowProfileDialogAsync(ServerProfile? editing, XamlRoot xamlRoot)
    {
        var result = await ServerProfileDialog.ShowAsync(editing, new ServerProfileDialogContext
        {
            XamlRoot = xamlRoot,
            WindowHandle = _windowHandle,
            MutedTextBrush = (Brush)Application.Current.Resources["MutedTextBrush"],
            HasSavedCredential = editing is not null && _hasSavedCredential(editing),
            ExistingProfiles = _profiles
        });
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
        EmptyStateTitle.Text = _profiles.Count == 0 ? "还没有服务器配置" : "没有匹配的服务器";
        EmptyStateDescription.Text = _profiles.Count == 0
            ? "添加服务器后，可以在这里集中管理连接信息。"
            : "尝试搜索其他名称、主机地址或用户名。";
    }
}
