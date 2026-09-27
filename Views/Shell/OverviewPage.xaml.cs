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
        ConnectionSectionTitle.Text = state.Mode == OverviewConnectionMode.Recent
            ? "最近连接" : "已保存服务器";
        ConnectionSectionTitle.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;

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
