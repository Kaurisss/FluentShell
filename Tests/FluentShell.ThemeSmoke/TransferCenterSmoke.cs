using System.Reflection;
using FluentShell.Core;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private static void TransferCommand(TransferTask task, string method, params object[] arguments) =>
        typeof(TransferTask).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, arguments);

    private static void PublishTransfer(TransferTask task, long bytes)
    {
        var snapshot = new SftpSessionSnapshot(SftpSessionState.Idle, SftpDirectoryListing.Empty("/"), true, true, false, "", null)
        {
            Transfer = new(SftpTransferState.Transferring, "正在下载 cluster_1/Caves/save/event_match_stats.bin…", new(bytes, 100, 512)),
            Queue = new([new("cluster_1/Caves/save/event_match_stats.bin", 100, TransferItemState.Transferring, bytes)], 1, 0, 0, 0)
        };
        TransferCommand(task, "Update", snapshot);
    }

    private static void InvokeTransferButton(TransferTaskCard card, string name) =>
        ((IInvokeProvider)new ButtonAutomationPeer((Button)card.FindName(name)).GetPattern(PatternInterface.Invoke)).Invoke();

    private static TransferTaskCard TransferCard(TransferCenterView view, TransferTask task) =>
        Descendants((DependencyObject)((ListView)view.FindName("TaskList")).ContainerFromItem(task))
            .OfType<TransferTaskCard>().Single();

    private async Task VerifyTransferCenterAsync()
    {
        void Check(bool passed, string description)
        {
            Program.Results.Add(new { control = description, passed });
            if (!passed) throw new InvalidOperationException(description);
        }

        _window = new Window { Title = "FluentShell offline transfer center regression" };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 800));
        var root = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  RequestedTheme="Light" Background="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
            """);
        var host = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Padding="16,16,0,16" CornerRadius="8" BorderThickness="1" HorizontalAlignment="Center"
                    VerticalAlignment="Top" Margin="24" Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                    BorderBrush="{ThemeResource SurfaceStrokeColorDefaultBrush}" />
            """);
        var center = new TransferCenter();
        var retryCount = 0;
        var task = center.Add(Guid.NewGuid(), "腾讯云 · 会话 51d9c1", "下载", "cluster_1", @"C:\Users\Offline\Downloads",
            () => { retryCount++; return Task.CompletedTask; }, () => true);
        using var control = new TransferControl();
        TransferCommand(task, "Start", control);
        PublishTransfer(task, 25);
        var view = new TransferCenterView { Width = 520, Height = 500 };
        view.SetCenter(center);
        host.Child = view;
        root.Children.Add(host);
        _window.Content = root;
        _window.Activate();
        await Task.Delay(200);
        root.UpdateLayout();
        var card = TransferCard(view, task);
        var expander = (Expander)card.FindName("FileDetails");
        var pauseButton = (Button)card.FindName("PauseButton");
        var filter = (ComboBox)view.FindName("FilterBox");
        Check(((TransferFilterOption)filter.Items[0]).Label == "全部任务（1）"
            && Descendants(filter).OfType<TextBlock>().Any(text => text.Text == "全部任务（1）"), "Collapsed filter displays the current task count");
        Check(card.Task == task && ((ProgressBar)card.FindName("TaskProgress")).Value == 25, "Task card binds the real task and progress");
        Check(Grid.GetRow((StackPanel)card.FindName("TaskActions")) == 0, "Wide cards place actions beside the progress information");
        Check(pauseButton.Focus(FocusState.Keyboard), "Primary transfer action accepts keyboard focus");
        expander.IsExpanded = true;
        await Task.Delay(150);
        PublishTransfer(task, 50);
        await Task.Delay(100);
        Check(ReferenceEquals(card, TransferCard(view, task)) && expander.IsExpanded,
            "Progress updates preserve the card and expanded file details");
        Check(ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), pauseButton), "Progress updates preserve keyboard focus");
        Check(Descendants(expander).OfType<TextBlock>().Any(text => text.Text == "50%"), "Expanded file details update their percentage");
        expander.IsExpanded = false;
        await Task.Delay(250);
        InvokeTransferButton(card, "PauseButton");
        await Task.Delay(100);
        Check(task.State == TransferTaskState.Paused && ((TextBlock)card.FindName("StateText")).Text == "已暂停"
            && Descendants(pauseButton).OfType<TextBlock>().Any(text => text.Text == "继续"), "Pause updates the badge and resume action");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            await Task.Delay(100);
            root.UpdateLayout();
            Check(card.ActualTheme == theme, $"Task cards follow the {theme} theme");
            var surface = (Border)card.FindName("CardSurface");
            Check(surface.CornerRadius == (CornerRadius)Resources["ControlCornerRadius"] && host.CornerRadius == new CornerRadius(8),
                $"{theme} preserves the existing control and flyout corner radii");
            await CaptureAsync(host, Program.ReportPath + $".{theme}.wide.png");
            view.Width = 280;
            await Task.Delay(100);
            root.UpdateLayout();
            Check(Grid.GetRow((StackPanel)card.FindName("TaskActions")) == 1, "Narrow cards move actions onto their own row");
            foreach (var name in new[] { "PauseButton", "DiscardButton", "StatusBadge" })
            {
                var element = (FrameworkElement)card.FindName(name);
                var position = element.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
                Check(position.X >= 0 && position.X + element.ActualWidth <= card.ActualWidth + 1,
                    $"{theme} narrow {name} stays within the card");
            }
            await CaptureAsync(host, Program.ReportPath + $".{theme}.narrow.png");
            view.Width = 520;
            await Task.Delay(100);
        }
        InvokeTransferButton(card, "PauseButton");
        await Task.Delay(100);
        Check(task.State == TransferTaskState.Running, "Resume continues the original transfer");
        filter.SelectedIndex = 2;
        await Task.Delay(100);
        Check(((FrameworkElement)view.FindName("EmptyPanel")).Visibility == Visibility.Visible
            && ((TextBlock)view.FindName("EmptyTitle")).Text == "暂无已结束的任务", "Finished filter shows an accurate empty state");
        filter.SelectedIndex = 0;
        await Task.Delay(100);
        card = TransferCard(view, task);
        TransferCommand(task, "Finish", true, "模拟失败：没有写入权限。");
        await Task.Delay(100);
        Check(((Button)card.FindName("RetryButton")).Visibility == Visibility.Visible
            && ((Button)card.FindName("PauseButton")).Visibility == Visibility.Collapsed, "Failed tasks expose retry and hide pause");
        await CaptureAsync(host, Program.ReportPath + ".failed.png");
        InvokeTransferButton(card, "RetryButton");
        await Task.Delay(100);
        Check(retryCount == 1, "Retry invokes the original task command");
        filter.SelectedIndex = 1;
        await Task.Delay(100);
        Check(((FrameworkElement)view.FindName("EmptyPanel")).Visibility == Visibility.Visible, "Active filter excludes failed tasks");
        filter.SelectedIndex = 2;
        await Task.Delay(100);
        card = TransferCard(view, task);
        InvokeTransferButton(card, "DiscardButton");
        await Task.Delay(100);
        Check(center.Groups.Count == 0 && ((TransferFilterOption)filter.Items[0]).Label == "全部任务（0）",
            "Discard removes the original task and updates filter counts");
        filter.SelectedIndex = 0;
        await CaptureAsync(host, Program.ReportPath + ".empty.png");

        var scanning = center.Add(Guid.NewGuid(), "腾讯云 · 会话 51d9c1", "下载", "cluster_1", @"C:\Users\Offline\Downloads",
            () => Task.CompletedTask, () => true);
        using var scanningControl = new TransferControl();
        TransferCommand(scanning, "Start", scanningControl);
        TransferCommand(scanning, "Update", new SftpSessionSnapshot(SftpSessionState.Idle, SftpDirectoryListing.Empty("/"), true, true, false, "", null)
        {
            Transfer = new(SftpTransferState.Transferring, "正在统计 cluster_1/Caves/save/event_match_stats.bin…（已发现 41 项）", null)
        });
        await Task.Delay(100);
        card = TransferCard(view, scanning);
        Check(((ProgressBar)card.FindName("TaskProgress")).IsIndeterminate, "Unknown totals keep the native indeterminate progress indicator");
        InvokeTransferButton(card, "PauseButton");
        await Task.Delay(100);
        Check(!((ProgressBar)card.FindName("TaskProgress")).IsIndeterminate
            && Descendants(card).OfType<TextBlock>().Any(text => text.Text.Contains("已发现 41 项")),
            "Paused discovery keeps its actual message and stops the indeterminate animation");
        root.RequestedTheme = ElementTheme.Light;
        await Task.Delay(100);
        await CaptureAsync(host, Program.ReportPath + ".discovery.png");

        var anchor = new Button { Content = "Offline popup", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        root.Children.Add(anchor);
        host.Child = null;
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.MaxWidthProperty, 640d));
        presenterStyle.Setters.Add(new Setter(Control.MaxHeightProperty, 10000d));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 16, 0, 16)));
        var flyout = new Flyout { Content = view, FlyoutPresenterStyle = presenterStyle };
        flyout.ShowAt(anchor);
        await Task.Delay(150);
        Check(view.IsLoaded && view.ActualWidth > 0 && TransferCard(view, scanning).Task == scanning,
            "Transfer view reloads correctly inside a live native flyout");
        await HideFlyoutAsync(flyout);
        center.Discard(scanning);
        Program.Finish();
    }
}
