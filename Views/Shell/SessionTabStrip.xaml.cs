using FluentShell.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentShell.Views.Shell;

public sealed partial class SessionTabStrip : UserControl, ISessionTabStrip
{
    private readonly Dictionary<IShellSession, ToggleButton> _tabButtons = [];
    private readonly Dictionary<IShellSession, Grid> _tabContainers = [];
    private bool _updatingSelection;

    public SessionTabStrip()
    {
        InitializeComponent();
    }

    public event EventHandler? NewSessionRequested;
    public event EventHandler<IShellSession>? SessionSelected;
    public event EventHandler<IShellSession>? SessionCloseRequested;

    public void Add(IShellSession session)
    {
        var presentation = SessionTabPresentation.For(session);
        var container = new Grid
        {
            Height = 40,
            MinWidth = 132,
            MaxWidth = 240
        };
        var tabButton = new ToggleButton
        {
            Tag = session,
            Content = new TextBlock
            {
                Text = presentation.Title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 168
            },
            Style = (Style)Application.Current.Resources["TitleBarSessionTabStyle"],
            Padding = new Thickness(12, 0, 40, 0),
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
            Content = new FontIcon { Glyph = "\uE711", FontSize = 14 },
            Style = (Style)Application.Current.Resources["TitleBarSessionIconButtonStyle"],
            Width = 32,
            Height = 32,
            MinWidth = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
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

    public void Select(IShellSession session)
    {
        _updatingSelection = true;
        foreach (var (candidate, button) in _tabButtons)
            button.IsChecked = ReferenceEquals(candidate, session);
        _updatingSelection = false;
    }

    public void Remove(IShellSession session)
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
