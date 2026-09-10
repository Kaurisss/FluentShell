using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class OverviewPage : UserControl
{
    private double _contentHorizontalSpacing;

    public OverviewPage()
    {
        InitializeComponent();
    }

    public event EventHandler? ConnectServerRequested;
    public event EventHandler? AddServerRequested;
    public event EventHandler<ServerProfile>? ConnectRequested;

    public void UpdateResponsiveLayout(double contentHorizontalSpacing)
    {
        _contentHorizontalSpacing = Math.Max(0, contentHorizontalSpacing);
        RootScrollViewer.Margin = new Thickness(0, 0, -contentHorizontalSpacing, 0);
        UpdateContentWidth();
        if (RootContent.Width > 0 && RootContent.Width < 720)
        {
            ActionButtons.Orientation = Orientation.Vertical;
            ActionButtons.HorizontalAlignment = HorizontalAlignment.Stretch;
            SummaryGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SummaryGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            SummaryGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ActionButtons.Orientation = Orientation.Horizontal;
            ActionButtons.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void RootScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentWidth();

    private void UpdateContentWidth()
    {
        var viewportWidth = RootScrollViewer.ViewportWidth > 0
            ? RootScrollViewer.ViewportWidth
            : RootScrollViewer.ActualWidth;
        if (viewportWidth <= 0) return;

        var contentWidth = Math.Max(0, viewportWidth - _contentHorizontalSpacing);
        RootContent.Width = Math.Min(1080, contentWidth);
    }

    public void SetOverview(IReadOnlyList<ServerProfile> profiles)
    {
        var state = OverviewConnectionQuery.Apply(profiles);
        var hasProfiles = state.Mode != OverviewConnectionMode.Empty;
        var recentCount = profiles.Count(profile => profile.LastConnectedAt is not null);
        SavedCountText.Text = profiles.Count.ToString();
        RecentCountText.Text = recentCount.ToString();
        ConnectionStatusText.Text = hasProfiles ? "可连接" : "待配置";

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
