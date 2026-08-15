using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public sealed partial class ConnectedServerSidebar : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _metricElements = [];
    private readonly Dictionary<string, double> _compactMetricValues = [];
    private Guid? _currentProfileId;
    private SessionConnectionState _connectionState = SessionConnectionState.Disconnected;
    private bool _hasFreshMetrics;

    public ConnectedServerSidebar()
    {
        InitializeComponent();
        UpdateCompactPresentation();
    }

    public event EventHandler? ReconnectRequested;

    public void SetPaneOpen(bool isPaneOpen)
    {
        ExpandedPanel.Visibility = isPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        CompactPanel.Visibility = isPaneOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateSession(ServerProfile profile, SessionConnectionState connectionState)
    {
        var profileChanged = _currentProfileId != profile.Id;
        _currentProfileId = profile.Id;
        _connectionState = connectionState;
        if (profileChanged || connectionState != SessionConnectionState.Connected)
            ClearMetricPresentation();

        ServerNameText.Text = profile.Name;
        ServerStatusText.Text = connectionState switch
        {
            SessionConnectionState.Connected => "已连接",
            SessionConnectionState.Connecting => "连接中…",
            _ => "已断开"
        };
        ReconnectButton.Visibility = connectionState == SessionConnectionState.Disconnected
            ? Visibility.Visible
            : Visibility.Collapsed;
        AddressText.Text = profile.Address;
        UserText.Text = $"用户：{profile.Username}";
        AutomationProperties.SetName(CompactReconnectButton, $"重新连接 {profile.Name}");
        ToolTipService.SetToolTip(CompactReconnectButton, $"重新连接 {profile.Name}");
        UpdateCompactPresentation();
    }

    public void UpdateMetrics(Guid profileId, ServerMetrics metrics, bool showDetails)
    {
        if (_currentProfileId != profileId || _connectionState != SessionConnectionState.Connected)
            return;

        _hasFreshMetrics = true;
        UpdateCompactPresentation();
        BuildProgressMetric("CPU", metrics.CpuPercent);
        BuildProgressMetric("内存", metrics.MemoryPercent);
        BuildProgressMetric("Swap", metrics.SwapPercent);
        CompactCpuText.Text = FormatPercent(metrics.CpuPercent);
        CompactMemoryText.Text = FormatPercent(metrics.MemoryPercent);
        CompactSwapText.Text = FormatPercent(metrics.SwapPercent);
        UpdateCompactMetric("CPU", metrics.CpuPercent);
        UpdateCompactMetric("内存", metrics.MemoryPercent);
        UpdateCompactMetric("Swap", metrics.SwapPercent);

        if (!showDetails) return;
        BuildTextMetric("负载", metrics.LoadAverage);
        BuildTextMetric("系统", metrics.OperatingSystem);
        BuildTextMetric("主机名", metrics.Hostname);
        BuildTextMetric("运行时间", metrics.Uptime);
    }

    private void UpdateCompactPresentation()
    {
        var isDisconnected = _connectionState == SessionConnectionState.Disconnected;
        var isConnecting = _connectionState == SessionConnectionState.Connecting;

        CompactReconnectPanel.Visibility = isDisconnected ? Visibility.Visible : Visibility.Collapsed;
        CompactConnectingPanel.Visibility = isConnecting ? Visibility.Visible : Visibility.Collapsed;
        CompactConnectionProgressRing.IsActive = isConnecting;
        CompactMetricsPanel.Visibility = _connectionState == SessionConnectionState.Connected
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_hasFreshMetrics)
            ClearCompactMetricValues();
    }

    private void ClearMetricPresentation()
    {
        _hasFreshMetrics = false;
        _compactMetricValues.Clear();
        _metricElements.Clear();
        MetricsPanel.Children.Clear();
        ClearCompactMetricValues();
    }

    private void ClearCompactMetricValues()
    {
        CompactCpuText.Text = string.Empty;
        CompactMemoryText.Text = string.Empty;
        CompactSwapText.Text = string.Empty;
        CompactCpuFill.Width = 0;
        CompactMemoryFill.Width = 0;
        CompactSwapFill.Width = 0;
    }

    private void ReconnectButton_Click(object sender, RoutedEventArgs e) =>
        ReconnectRequested?.Invoke(this, EventArgs.Empty);

    private void CompactPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var metric in _compactMetricValues)
            UpdateCompactMetric(metric.Key, metric.Value);
    }

    private void UpdateCompactMetric(string label, double? value)
    {
        var (track, fill) = label switch
        {
            "CPU" => (CompactCpuTrack, CompactCpuFill),
            "内存" => (CompactMemoryTrack, CompactMemoryFill),
            "Swap" => (CompactSwapTrack, CompactSwapFill),
            _ => (null, null)
        };
        if (track is null || fill is null) return;

        if (value is null)
        {
            _compactMetricValues.Remove(label);
            fill.Width = 0;
            fill.Height = track.ActualHeight;
            return;
        }

        _compactMetricValues[label] = Math.Clamp(value.Value, 0, 100);
        fill.Width = track.ActualWidth * _compactMetricValues[label] / 100d;
        fill.Height = track.ActualHeight;
    }

    private static string FormatPercent(double? value) =>
        value is null ? string.Empty : $"{value:0}%";

    private void BuildProgressMetric(string label, double? value)
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
        ((TextBlock)((Grid)children[0]).Children[1]).Text = FormatPercent(value);
        ((ProgressBar)children[1]).Value = value ?? 0;
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