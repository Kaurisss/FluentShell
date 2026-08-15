using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class OverviewPage : UserControl
{
    public OverviewPage()
    {
        InitializeComponent();
    }

    public event EventHandler? ConnectServerRequested;
    public event EventHandler? AddServerRequested;
    public event EventHandler<ServerProfile>? ConnectRequested;

    public void SetOverview(IReadOnlyList<ServerProfile> profiles)
    {
        var state = OverviewConnectionQuery.Apply(profiles);
        var hasProfiles = state.Mode != OverviewConnectionMode.Empty;

        (ConnectionSectionTitle.Text, ConnectionSectionDescription.Text) = state.Mode switch
        {
            OverviewConnectionMode.Recent => (
                "最近连接",
                "继续使用最近成功连接过的服务器。"),
            OverviewConnectionMode.SavedFallback => (
                "可连接的服务器",
                "选择一台已保存的服务器，建立新的 SSH 会话。"),
            _ => (
                "开始连接",
                "先添加一台服务器配置，再从这里建立 SSH 会话。")
        };

        ConnectServerButton.IsEnabled = hasProfiles;
        ConnectionCards.ItemsSource = state.Profiles;
        ConnectionCards.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConnectServerButton_Click(object sender, RoutedEventArgs e) =>
        ConnectServerRequested?.Invoke(this, EventArgs.Empty);

    private void AddServerButton_Click(object sender, RoutedEventArgs e) =>
        AddServerRequested?.Invoke(this, EventArgs.Empty);

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ServerProfile profile)
            ConnectRequested?.Invoke(this, profile);
    }
}