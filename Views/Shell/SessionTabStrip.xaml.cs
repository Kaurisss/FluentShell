using FluentShell.Core;
using FluentShell.Views.Converters;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace FluentShell.Views.Shell;

public sealed partial class SessionTabStrip : UserControl, ISessionTabStrip
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out PointInt32 point);
    // Official Fluent System Icons: caret_left_12_filled and caret_right_12_filled.
    private const string CaretLeftPath = "M6.29863 3.28162C6.93081 2.65918 8.00024 3.10702 8.00024 3.99419V8.0062C8.00024 8.89338 6.9308 9.34122 6.29863 8.71877L4.26129 6.71276C3.86342 6.32102 3.86343 5.67936 4.26129 5.28763L6.29863 3.28162Z";
    private const string CaretRightPath = "M5.7016 3.28162C5.06943 2.65918 4 3.10702 4 3.99419V8.0062C4 8.89338 5.06944 9.34122 5.70161 8.71877L7.73895 6.71276C8.13681 6.32102 8.13681 5.67936 7.73895 5.28763L5.7016 3.28162Z";
    private readonly Dictionary<IShellSession, ToggleButton> _tabButtons = [];
    private readonly Dictionary<IShellSession, Grid> _tabContainers = [];
    private bool _updatingSelection;
    private bool _visibilityUpdateQueued;
    private IShellSession? _selectedSession;
    private double? _pendingScrollOffset;
    private InputPointerSource? _pointerSource;

    public SessionTabStrip()
    {
        InitializeComponent();
        CaretLeftIcon.Data = IconGeometryConverter.Parse(CaretLeftPath, 12);
        CaretRightIcon.Data = IconGeometryConverter.Parse(CaretRightPath, 12);
    }

    public event EventHandler? NewSessionRequested;
    public event EventHandler<IShellSession>? SessionSelected;
    public event EventHandler<IShellSession>? SessionCloseRequested;

    internal FrameworkElement TitleBarInputElement => TabStripLayout;

    public void Add(IShellSession session)
    {
        var presentation = SessionTabPresentation.For(session);
        var container = new Grid
        {
            Height = 32,
            MinWidth = 140,
            MaxWidth = 240
        };
        var tabButton = new SessionTabButton
        {
            Tag = session,
            Content = new TextBlock
            {
                Text = presentation.Title,
                Style = (Style)Application.Current.Resources["TitleBarSessionTabInactiveTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 168
            },
            Style = (Style)Application.Current.Resources["TitleBarSessionTabStyle"],
            Padding = new Thickness(12, 0, 36, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTipService.SetToolTip(tabButton, presentation.ToolTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            tabButton,
            presentation.SelectAccessibleName);
        tabButton.Checked += TabButton_Checked;
        container.Children.Add(tabButton);

        var closeButton = new Button
        {
            Tag = session,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"],
            Width = 28,
            Height = 32,
            MinWidth = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        ToolTipService.SetToolTip(closeButton, presentation.CloseToolTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            closeButton,
            presentation.CloseAccessibleName);
        closeButton.Click += CloseButton_Click;
        container.Children.Add(closeButton);

        _tabButtons[session] = tabButton;
        _tabContainers[session] = container;
        TabPanel.Children.Add(container);
    }

    public void Select(IShellSession? session)
    {
        _selectedSession = session;
        _updatingSelection = true;
        foreach (var (candidate, button) in _tabButtons)
        {
            var isActive = ReferenceEquals(candidate, session);
            button.IsChecked = isActive;
            if (button.Content is TextBlock title)
                title.Style = (Style)Application.Current.Resources[isActive
                    ? "TitleBarSessionTabActiveTextStyle"
                    : "TitleBarSessionTabInactiveTextStyle"];
        }
        _updatingSelection = false;
        QueueSelectedTabVisibility();
    }

    public void Remove(IShellSession session)
    {
        if (_tabButtons.Remove(session, out var tabButton))
            tabButton.Checked -= TabButton_Checked;
        if (!_tabContainers.Remove(session, out var container)) return;

        foreach (var button in container.Children.OfType<Button>())
            button.Click -= CloseButton_Click;
        TabPanel.Children.Remove(container);
        if (ReferenceEquals(_selectedSession, session)) _selectedSession = null;
    }

    private void TabStrip_Loaded(object sender, RoutedEventArgs e)
    {
        if (_pointerSource is null)
        {
            // Title-bar wheel input may be consumed before XAML's routed event.
            // Listen to the island while loaded and filter to the tab viewport below.
            _pointerSource = InputPointerSource.GetForIsland(XamlRoot.ContentIsland);
            _pointerSource.PointerWheelChanged += TabStrip_PointerWheelChanged;
        }
        UpdateTabLayout();
    }

    private void TabStrip_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_pointerSource is not null)
            _pointerSource.PointerWheelChanged -= TabStrip_PointerWheelChanged;
        _pointerSource = null;
        _pendingScrollOffset = null;
    }

    private void TabLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabLayout();

    private void UpdateTabLayout()
    {
        // Compare against the space without arrows so they disappear as soon as all tabs fit.
        var addButtonWidth = NewSessionButton.Width + NewSessionButton.Margin.Left + NewSessionButton.Margin.Right;
        var availableWidth = ActualWidth - addButtonWidth;
        var overflow = TabPanel.ActualWidth > Math.Max(0, availableWidth) + 1;
        ScrollLeftButton.Visibility = ScrollRightButton.Visibility = overflow
            ? Visibility.Visible : Visibility.Collapsed;
        // Keep the add command next to the last tab; consume the available width only during overflow.
        var controlsWidth = addButtonWidth + (overflow ? ScrollLeftButton.Width + ScrollRightButton.Width : 0);
        TabStripLayout.Width = Math.Max(0, Math.Min(ActualWidth, TabPanel.ActualWidth + controlsWidth));
        UpdateScrollButtons();
        QueueSelectedTabVisibility();
    }

    private void QueueSelectedTabVisibility()
    {
        if (_visibilityUpdateQueued) return;
        _visibilityUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _visibilityUpdateQueued = false;
            EnsureSelectedTabVisible();
        });
    }

    private void EnsureSelectedTabVisible()
    {
        if (_selectedSession is null || !_tabContainers.TryGetValue(_selectedSession, out var container) ||
            container.ActualWidth <= 0 || TabScrollViewer.ViewportWidth <= 0)
            return;

        var left = container.TransformToVisual(TabPanel).TransformPoint(new Windows.Foundation.Point()).X;
        var right = left + container.ActualWidth;
        var offset = TabScrollViewer.HorizontalOffset;
        if (left < offset || container.ActualWidth > TabScrollViewer.ViewportWidth)
            ScrollTo(left);
        else if (right > offset + TabScrollViewer.ViewportWidth)
            ScrollTo(right - TabScrollViewer.ViewportWidth);
    }

    private void TabScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate) _pendingScrollOffset = null;
        UpdateScrollButtons();
    }

    private void TabStrip_PointerWheelChanged(InputPointerSource sender, PointerEventArgs e)
    {
        if (TabScrollViewer.ScrollableWidth <= 1) return;
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element && element.Visibility != Visibility.Visible) return;
        var bounds = TabScrollViewer.TransformToVisual(XamlRoot.Content).TransformBounds(
            new Windows.Foundation.Rect(0, 0, TabScrollViewer.ActualWidth, TabScrollViewer.ActualHeight));
        // Map the actual mouse position into XAML's coordinate space for hit testing.
        if (!GetPhysicalCursorPos(out var screenPoint)) return;
        var position = XamlRoot.CoordinateConverter.ConvertScreenToLocal(screenPoint);
        if (!bounds.Contains(position)) return;
        var properties = e.CurrentPoint.Properties;
        if (properties.MouseWheelDelta == 0) return;

        var delta = properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : -properties.MouseWheelDelta;
        var offset = _pendingScrollOffset ?? TabScrollViewer.HorizontalOffset;
        ScrollTo(offset + delta / 120d * (140 + TabPanel.Spacing));
        e.Handled = true;
    }

    private void UpdateScrollButtons()
    {
        ScrollLeftButton.IsEnabled = TabScrollViewer.HorizontalOffset > 1;
        ScrollRightButton.IsEnabled = TabScrollViewer.HorizontalOffset < TabScrollViewer.ScrollableWidth - 1;
    }

    private void ScrollTo(double offset)
    {
        var target = Math.Clamp(offset, 0, TabScrollViewer.ScrollableWidth);
        _pendingScrollOffset = target;
        if (!TabScrollViewer.ChangeView(target, null, null, disableAnimation: true))
            _pendingScrollOffset = null;
    }

    private void ScrollLeftButton_Click(object sender, RoutedEventArgs e) =>
        ScrollTo((_pendingScrollOffset ?? TabScrollViewer.HorizontalOffset) - Math.Max(140, TabScrollViewer.ViewportWidth * 0.75));

    private void ScrollRightButton_Click(object sender, RoutedEventArgs e) =>
        ScrollTo((_pendingScrollOffset ?? TabScrollViewer.HorizontalOffset) + Math.Max(140, TabScrollViewer.ViewportWidth * 0.75));

    private void NewSessionButton_Click(object sender, RoutedEventArgs e) =>
        NewSessionRequested?.Invoke(this, EventArgs.Empty);

    private void TabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection || (sender as ToggleButton)?.Tag is not IShellSession session)
            return;
        SessionSelected?.Invoke(this, session);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IShellSession session)
            SessionCloseRequested?.Invoke(this, session);
    }
}

/// <summary>点击只请求选中；取消选中由会话协调器通过 Select 控制。</summary>
internal sealed class SessionTabButton : ToggleButton
{
    protected override void OnToggle()
    {
        // 鼠标、键盘和自动化触发均保留当前标签的选中状态。
        if (IsChecked != true)
            base.OnToggle();
    }
}
