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

    public void SetOverview(IReadOnlyList<ServerProfile> profiles, string lastResult)
    {
        SavedCountText.Text = profiles.Count.ToString();
        var recent = profiles
            .Where(server => server.LastConnectedAt is not null)
            .OrderByDescending(server => server.LastConnectedAt)
            .FirstOrDefault();
        RecentServerText.Text = recent?.Name ?? "暂无";
        RecentServerDetailText.Text = recent is null
            ? "连接成功后会显示在这里"
            : recent.LastConnectedLabel;
        LastResultText.Text = lastResult;
        LastResultDetailText.Text = recent is null ? "还没有连接记录" : recent.Address;
    }

    public void UpdateResponsiveLayout(bool isNarrow)
    {
        StatsGrid.ColumnSpacing = isNarrow ? 0 : 16;
        StatsGrid.RowSpacing = isNarrow ? 12 : 0;
        StatsGrid.RowDefinitions[1].Height = isNarrow ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(RecentCard, isNarrow ? 1 : 0);
        Grid.SetColumn(RecentCard, isNarrow ? 0 : 1);
    }
}