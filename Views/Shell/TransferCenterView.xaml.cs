using System.Collections.ObjectModel;
using FluentShell.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class TransferCenterView : UserControl
{
    private TransferCenter? _center;
    private readonly ObservableCollection<TransferConnectionGroup> _visibleGroups = [];
    public event EventHandler? CloseRequested;

    public TransferCenterView()
    {
        InitializeComponent();
        GroupedTasks.Source = _visibleGroups;
        Loaded += (_, _) =>
        {
            if (_center is null) return;
            _center.Changed += Center_Changed;
            Refresh();
        };
        Unloaded += (_, _) => { if (_center is not null) _center.Changed -= Center_Changed; };
    }

    public void SetCenter(TransferCenter center) => _center = center;

    private void Center_Changed(object? sender, EventArgs e) => Refresh();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Pause_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as TransferTask)?.TogglePause();
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferTask task) await task.RetryAsync();
    }
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferTask task) _center?.Discard(task);
    }

    private void Refresh()
    {
        if (_center is null || TaskList is null) return;
        // Diff membership only: progress updates must not recreate rows, lose focus or collapse details.
        foreach (var old in _visibleGroups.ToArray())
            if (!_center.Groups.Any(g => g.ConnectionId == old.ConnectionId)) _visibleGroups.Remove(old);
        foreach (var source in _center.Groups)
        {
            var wanted = source.Where(t => FilterBox.SelectedIndex switch
            {
                1 => t.IsActive,
                2 => !t.IsActive,
                _ => true
            }).ToList();
            var group = _visibleGroups.FirstOrDefault(g => g.ConnectionId == source.ConnectionId);
            if (wanted.Count == 0)
            {
                if (group is not null) _visibleGroups.Remove(group);
                continue;
            }
            if (group is null)
            {
                group = new(source.ConnectionId, source.Label);
                _visibleGroups.Add(group);
            }
            foreach (var old in group.ToArray()) if (!wanted.Contains(old)) group.Remove(old);
            foreach (var task in wanted) if (!group.Contains(task)) group.Add(task);
        }
        EmptyPanel.Visibility = _visibleGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
