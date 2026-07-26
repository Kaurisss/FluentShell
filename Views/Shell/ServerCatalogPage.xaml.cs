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
    private readonly Func<bool> _rememberCredentialsByDefault;
    private IReadOnlyList<ServerProfile> _profiles = [];
    private bool _initialized;

    public ServerCatalogPage(
        IntPtr windowHandle,
        Func<ServerProfile, bool> hasSavedCredential,
        Func<bool> rememberCredentialsByDefault)
    {
        _windowHandle = windowHandle;
        _hasSavedCredential = hasSavedCredential;
        _rememberCredentialsByDefault = rememberCredentialsByDefault;
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

    public void UpdateResponsiveLayout(bool isNarrow)
    {
        ListHeader.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.RowSpacing = isNarrow ? 10 : 0;
        Toolbar.RowDefinitions.Clear();
        if (isNarrow)
        {
            Toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Toolbar.ColumnDefinitions[0].Width = new GridLength(136);
            Toolbar.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetColumnSpan(SearchBox, 4);
            Grid.SetRow(SortComboBox, 1);
            Grid.SetColumn(SortComboBox, 0);
            Grid.SetRow(RefreshButton, 1);
            Grid.SetColumn(RefreshButton, 2);
            Grid.SetRow(AddButton, 1);
            Grid.SetColumn(AddButton, 3);
            AddButton.Content = "添加";
        }
        else
        {
            Toolbar.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Toolbar.ColumnDefinitions[1].Width = new GridLength(150);
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetColumnSpan(SearchBox, 1);
            Grid.SetRow(SortComboBox, 0);
            Grid.SetColumn(SortComboBox, 1);
            Grid.SetRow(RefreshButton, 0);
            Grid.SetColumn(RefreshButton, 2);
            Grid.SetRow(AddButton, 0);
            Grid.SetColumn(AddButton, 3);
            AddButton.Content = "添加服务器";
        }
    }

    public Task ShowAddDialogAsync(XamlRoot xamlRoot) => ShowProfileDialogAsync(null, xamlRoot);

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => NotifyFilterChanged();
    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => NotifyFilterChanged();

    private void ProfilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ServerProfile profile)
            ConnectRequested?.Invoke(this, profile);
    }

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
        var dialog = new ContentDialog
        {
            Title = "删除服务器",
            Content = $"确定删除“{profile.Name}”吗？不会影响远程主机。",
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
            RememberCredentialsByDefault = _rememberCredentialsByDefault()
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
        ProfilesList.ItemsSource = ServerProfileQuery.Apply(_profiles, SearchBox.Text, sortOrder);
    }
}