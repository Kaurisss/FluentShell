using System.ComponentModel;
using FluentShell.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class TransferTaskCard : UserControl
{
    public static readonly DependencyProperty TaskProperty = DependencyProperty.Register(
        nameof(Task), typeof(TransferTask), typeof(TransferTaskCard), new PropertyMetadata(null, Task_Changed));

    private TransferTask? _subscribedTask;
    public TransferTask? Task
    {
        get => (TransferTask?)GetValue(TaskProperty);
        set => SetValue(TaskProperty, value);
    }

    public event EventHandler<TransferTask>? DiscardRequested;

    public TransferTaskCard()
    {
        InitializeComponent();
        Loaded += (_, _) => { Subscribe(); UpdateState(); UpdateLayoutState(); };
        Unloaded += (_, _) => Unsubscribe();
        SizeChanged += (_, _) => UpdateLayoutState();
    }

    private static void Task_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var card = (TransferTaskCard)sender;
        card.Unsubscribe();
        if (card.IsLoaded) card.Subscribe();
        card.UpdateState();
    }

    private void Subscribe()
    {
        if (ReferenceEquals(_subscribedTask, Task)) return;
        _subscribedTask = Task;
        if (_subscribedTask is not null) _subscribedTask.PropertyChanged += Progress_Changed;
    }

    private void Unsubscribe()
    {
        if (_subscribedTask is not null) _subscribedTask.PropertyChanged -= Progress_Changed;
        _subscribedTask = null;
    }

    private void Progress_Changed(object? sender, PropertyChangedEventArgs e) => UpdateState();
    private void UpdateState() => VisualStateManager.GoToState(this, Task?.State.ToString() ?? "Running", false);
    private void UpdateLayoutState() => VisualStateManager.GoToState(this, ActualWidth < 420 ? "Compact" : "Wide", false);
    private void Pause_Click(object sender, RoutedEventArgs e) => Task?.TogglePause();
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Task is { } task) await task.RetryAsync();
    }
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (Task is { } task) DiscardRequested?.Invoke(this, task);
    }

    private string TargetLabel(string direction, string target) => $"{direction}  ·  {target}";
    private string ProgressText(long totalBytes, string progress, string message, bool isActive) =>
        totalBytes == 0 && isActive ? message : progress;
    private string CurrentPath(TransferQueue? queue) =>
        queue?.Items.FirstOrDefault(item => item.State == TransferItemState.Transferring)?.RelativePath ?? string.Empty;
    private Visibility CurrentPathVisibility(TransferQueue? queue) =>
        string.IsNullOrEmpty(CurrentPath(queue)) ? Visibility.Collapsed : Visibility.Visible;
    private Visibility OutcomeVisibility(bool isActive) => isActive ? Visibility.Collapsed : Visibility.Visible;
    private Symbol PauseSymbol(TransferTaskState state) => state == TransferTaskState.Paused ? Symbol.Play : Symbol.Pause;
    private Symbol ItemSymbol(TransferTaskKind kind) => kind switch
    {
        TransferTaskKind.Folder => Symbol.Folder,
        TransferTaskKind.Batch => Symbol.Copy,
        _ => Symbol.Document
    };
    private Symbol StatusSymbol(TransferTaskState state) => state switch
    {
        TransferTaskState.Paused => Symbol.Pause,
        TransferTaskState.Completed => Symbol.Accept,
        TransferTaskState.Failed or TransferTaskState.Disconnected => Symbol.Important,
        TransferTaskState.Discarded => Symbol.Delete,
        _ => Symbol.Sync
    };
}
