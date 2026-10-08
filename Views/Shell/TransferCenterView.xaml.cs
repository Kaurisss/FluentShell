using System.Collections.ObjectModel;
using System.ComponentModel;
using FluentShell.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class TransferCenterView : UserControl
{
    private TransferCenter? _center;
    private readonly ObservableCollection<TransferConnectionGroup> _visibleGroups = [];
    private readonly TransferFilterOption[] _filters =
        [new("全部任务（0）"), new("进行中（0，含暂停）"), new("已结束（0）")];
    public event EventHandler? CloseRequested;

    public TransferCenterView()
    {
        InitializeComponent();
        GroupedTasks.Source = _visibleGroups;
        FilterBox.ItemsSource = _filters;
        FilterBox.SelectedIndex = 0;
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
    private void TaskCard_DiscardRequested(object? sender, TransferTask task) => _center?.Discard(task);

    private void Refresh()
    {
        if (_center is null || TaskList is null) return;
        var tasks = _center.Groups.SelectMany(g => g).ToList();
        var activeCount = tasks.Count(t => t.IsActive);
        _filters[0].Label = $"全部任务（{tasks.Count}）";
        _filters[1].Label = $"进行中（{activeCount}，含暂停）";
        _filters[2].Label = $"已结束（{tasks.Count - activeCount}）";
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
        EmptyTitle.Text = FilterBox.SelectedIndex switch
        {
            1 => "暂无进行中的任务",
            2 => "暂无已结束的任务",
            _ => "暂无传输任务"
        };
        EmptyDescription.Text = FilterBox.SelectedIndex == 0
            ? "各服务器的上传和下载任务会统一显示在这里。"
            : "可以切换筛选条件，查看其他传输任务。";
    }

}

public sealed class TransferFilterOption(string label) : INotifyPropertyChanged
{
    private string _label = label;
    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
