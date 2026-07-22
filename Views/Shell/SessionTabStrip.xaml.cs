using FluentShell.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentShell.Views.Shell;

public sealed partial class SessionTabStrip : UserControl
{
    private readonly Dictionary<SessionWorkspace, ToggleButton> _tabButtons = [];
    private readonly Dictionary<SessionWorkspace, Grid> _tabContainers = [];
    private bool _updatingSelection;

    public SessionTabStrip()
    {
        InitializeComponent();
    }

    public event EventHandler? NewSessionRequested;
    public event EventHandler<SessionWorkspace>? SessionSelected;
    public event EventHandler<SessionWorkspace>? SessionCloseRequested;

    public void Add(SessionWorkspace session)
    {
        var container = new Grid
        {
            Height = 40,
            MinWidth = 128,
            MaxWidth = 240
        };
        var tabButton = new ToggleButton
        {
            Tag = session,
            Content = new TextBlock
            {
                Text = session.DisplayTitle,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 168
            },
            Style = (Style)Application.Current.Resources["TitleBarSessionTabStyle"],
            Padding = new Thickness(12, 0, 40, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTipService.SetToolTip(tabButton, session.DisplayTitle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            tabButton,
            $"切换到 {session.DisplayTitle} 会话");
        tabButton.Checked += TabButton_Checked;
        container.Children.Add(tabButton);

        var closeButton = new Button
        {
            Tag = session,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 16 },
            Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        ToolTipService.SetToolTip(closeButton, "关闭会话");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            closeButton,
            $"关闭 {session.DisplayTitle} 会话");
        closeButton.Click += CloseButton_Click;
        container.Children.Add(closeButton);

        _tabButtons[session] = tabButton;
        _tabContainers[session] = container;
        TabPanel.Children.Insert(Math.Max(0, TabPanel.Children.Count - 1), container);
    }

    public void Select(SessionWorkspace session)
    {
        _updatingSelection = true;
        foreach (var (candidate, button) in _tabButtons)
            button.IsChecked = ReferenceEquals(candidate, session);
        _updatingSelection = false;
    }

    public void Remove(SessionWorkspace session)
    {
        if (_tabButtons.Remove(session, out var tabButton))
            tabButton.Checked -= TabButton_Checked;
        if (!_tabContainers.Remove(session, out var container)) return;

        foreach (var button in container.Children.OfType<Button>())
            button.Click -= CloseButton_Click;
        TabPanel.Children.Remove(container);
    }

    private void NewSessionButton_Click(object sender, RoutedEventArgs e) =>
        NewSessionRequested?.Invoke(this, EventArgs.Empty);

    private void TabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection || (sender as ToggleButton)?.Tag is not SessionWorkspace session)
            return;
        SessionSelected?.Invoke(this, session);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SessionWorkspace session)
            SessionCloseRequested?.Invoke(this, session);
    }
}