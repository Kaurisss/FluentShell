using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public sealed partial class ConnectedServerSidebar : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _metricElements = [];
    private readonly Dictionary<string, double> _compactMetricValues = [];

    public ConnectedServerSidebar()
    {
        InitializeComponent();
    }

    public void SetPaneOpen(bool isPaneOpen)
    {
        ExpandedPanel.Visibility = isPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        CompactPanel.Visibility = isPaneOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateSession(ServerProfile profile, bool isConnected)
    {
        ServerNameText.Text = profile.Name;
        ServerStatusText.Text = isConnected ? "已连接" : "已断开";
        AddressText.Text = profile.Address;
        UserText.Text = $"用户：{profile.Username}";
    }

    public void UpdateMetrics(ServerMetrics metrics, bool showDetails)
    {
        BuildProgressMetric("CPU", metrics.CpuPercent);
        BuildProgressMetric("内存", metrics.MemoryPercent);
        BuildProgressMetric("Swap", metrics.SwapPercent);
        CompactCpuText.Text = $"{metrics.CpuPercent:0}%";
        CompactMemoryText.Text = $"{metrics.MemoryPercent:0}%";
        CompactSwapText.Text = $"{metrics.SwapPercent:0}%";
        UpdateCompactMetric("CPU", metrics.CpuPercent);
        UpdateCompactMetric("内存", metrics.MemoryPercent);
        UpdateCompactMetric("Swap", metrics.SwapPercent);

        if (!showDetails) return;
        BuildTextMetric("负载", metrics.LoadAverage);
        BuildTextMetric("系统", metrics.OperatingSystem);
        BuildTextMetric("主机名", metrics.Hostname);
        BuildTextMetric("运行时间", metrics.Uptime);
    }

    private void CompactPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var metric in _compactMetricValues)
            UpdateCompactMetric(metric.Key, metric.Value);
    }

    private void UpdateCompactMetric(string label, double value)
    {
        _compactMetricValues[label] = Math.Clamp(value, 0, 100);
        var (track, fill) = label switch
        {
            "CPU" => (CompactCpuTrack, CompactCpuFill),
            "内存" => (CompactMemoryTrack, CompactMemoryFill),
            "Swap" => (CompactSwapTrack, CompactSwapFill),
            _ => (null, null)
        };
        if (track is null || fill is null) return;

        fill.Width = track.ActualWidth * _compactMetricValues[label] / 100d;
        fill.Height = track.ActualHeight;
    }

    private void BuildProgressMetric(string label, double value)
    {
        if (!_metricElements.TryGetValue(label, out var element))
        {
            var stack = new StackPanel { Spacing = 5 };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            var valueText = new TextBlock
            {
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
            };
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            var progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 4,
                Margin = new Thickness(0, 2, 0, 0)
            };
            stack.Children.Add(row);
            stack.Children.Add(progress);
            element = stack;
            _metricElements[label] = stack;
            MetricsPanel.Children.Add(stack);
        }

        var children = ((StackPanel)element).Children;
        ((TextBlock)((Grid)children[0]).Children[1]).Text = $"{value:0}%";
        ((ProgressBar)children[1]).Value = value;
    }

    private void BuildTextMetric(string label, string value)
    {
        if (!_metricElements.TryGetValue(label, out var element))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
            });
            var valueText = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            element = row;
            _metricElements[label] = row;
            MetricsPanel.Children.Add(row);
        }

        ((TextBlock)((Grid)element).Children[1]).Text = value;
    }
}