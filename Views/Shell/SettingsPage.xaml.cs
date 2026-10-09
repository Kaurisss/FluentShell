using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Views.Dialogs;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentShell.Views.Shell;

public sealed partial class SettingsPage : UserControl
{
    private readonly IntPtr _windowHandle;
    private bool _loading;
    private TerminalColors? _loadedColors;
    private readonly Dictionary<string, Button> _lightColors = [];
    private readonly Dictionary<string, Button> _darkColors = [];
    private UserPreferences _preferences = new();
    private AppSettings _currentSettings = new();
    private readonly List<Action<UserPreferences>> _optionLoaders = [];

    public SettingsPage(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        InitializeComponent();
        BuildColorFields(LightTerminalColorsPanel, LightAnsiColorsPanel, _lightColors, false);
        BuildColorFields(DarkTerminalColorsPanel, DarkAnsiColorsPanel, _darkColors, true);
        BuildPreferenceEditors();
        UpdateResponsiveLayout(30);
        SettingsBreadcrumb.ItemsSource = new[] { "设置" };
        VersionText.Text = $"版本 {Services.DiagnosticLog.ApplicationVersion}";
        DiagnosticSummaryText.Text = Services.DiagnosticLog.Summary;
    }

    public void UpdateResponsiveLayout(double horizontalSpacing)
    {
        // Keep the scrollbar at the page edge; only the scrollable content is inset.
        var inset = new Thickness(0, 0, horizontalSpacing, 0);
        SettingsHeader.Margin = inset;
        ((FrameworkElement)SettingsHome.Content).Margin = inset;
        foreach (var page in CategoryPages().Values)
            ((FrameworkElement)page.Page.Content).Margin = inset;
    }

    public event EventHandler<AppSettingsUpdate>? SettingsChanged;
    public event EventHandler? ClearLocalDataRequested;

    public void SetSettings(AppSettings settings, string dataFolder)
    {
        _loading = true;
        _currentSettings = settings;
        _preferences = (settings.Preferences ?? new()).Normalize();
        foreach (var load in _optionLoaders) load(_preferences);
        ThemeComboBox.SelectedIndex = settings.Theme switch
        {
            "浅色" => 1,
            "深色" => 2,
            _ => 0
        };
        BackdropMaterialComboBox.SelectedIndex = settings.BackdropMaterial == "亚克力" ? 1 : 0;
        TerminalFontSizeBox.Value = settings.TerminalFontSize;
        DownloadDirectoryBox.Text = settings.DownloadDirectory;
        DataLocationText.Text = dataFolder;
        if (!ReferenceEquals(_loadedColors, settings.TerminalColors))
        {
            _loadedColors = settings.TerminalColors;
            var colors = (settings.TerminalColors ?? new()).Normalize();
            LoadColorFields(_lightColors, colors.Light, false);
            LoadColorFields(_darkColors, colors.Dark, true);
        }
        _loading = false;
    }

    private bool _colorDialogOpen;

    private void OpenTerminalColors_Click(object sender, RoutedEventArgs e)
    {
        ShowCategory("terminal");
    }

    private void SettingsBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0) NavigateCategory(null);
    }

    private Dictionary<string, (ScrollViewer Page, string Title, string HelpText)> CategoryPages() => new()
    {
        ["appearance"] = (AppearancePage, "外观", string.Empty),
        ["terminal"] = (TerminalColorsPage, "终端", "点击色块选择颜色，在颜色选择器中点击“保存”或“使用默认”即保存并应用。浅色和深色配色可分别恢复默认，其他终端选项即时保存。"),
        ["connection"] = (ConnectionPage, "连接", "连接超时和保活间隔在新建或重新连接时生效；0 次重连表示关闭自动重连。主机密钥校验始终保留。"),
        ["transfer"] = (TransferSettingsPage, "文件传输", "并发上限作用于不同会话的传输批次；单个会话仍按顺序使用独立传输通道。冲突策略从下一批传输开始生效。"),
        ["shortcuts"] = (ShortcutSettingsPage, "快捷键", "点击快捷键后按下 Ctrl + Shift + 字母进行录入。各操作须使用不同组合，避免占用远端 Shell 的常用快捷键。"),
        ["data"] = (DataSettingsPage, "数据管理", "导入、导出和恢复默认仅针对应用设置，不包含或更改服务器配置、主机指纹及凭据。"),
        ["about"] = (AboutSettingsPage, "关于与诊断", "诊断信息包含应用版本、操作系统、进程架构和 .NET 版本，可在反馈问题时附上。日志不记录凭据或终端内容。")
    };

    private void Category_Click(object sender, RoutedEventArgs e) => ShowCategory((string)((FrameworkElement)sender).Tag);

    private void ShowCategory(string key)
    {
        NavigateCategory(key);
    }

    private string? _category;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _navigationAnimation;
    private FrameworkElement? _animatedPage;

    private void NavigateCategory(string? key)
    {
        if (_category == key) return;
        var previous = _category;
        _navigationAnimation?.Stop();
        if (_animatedPage is not null)
        {
            _animatedPage.Opacity = 1;
            _animatedPage.RenderTransform = null;
        }
        var pages = CategoryPages();
        PageHelpButton.Flyout.Hide();
        PageHelpText.Text = key is null ? string.Empty : pages[key].HelpText;
        PageHelpTitle.Text = key is null ? string.Empty : $"{pages[key].Title}说明";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PageHelpButton, PageHelpTitle.Text);
        ToolTipService.SetToolTip(PageHelpButton, PageHelpTitle.Text);
        PageHelpButton.Visibility = string.IsNullOrEmpty(PageHelpText.Text) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var page in pages.Values) page.Page.Visibility = Visibility.Collapsed;
        SettingsHome.Visibility = key is null ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement target = key is null ? SettingsHome : pages[key].Page;
        target.Visibility = Visibility.Visible;
        _category = key;
        SettingsBreadcrumb.ItemsSource = key is null ? new[] { "设置" } : new[] { "设置", pages[key].Title };
        if (key is null)
        {
            var home = (StackPanel)SettingsHome.Content;
            var card = previous == "terminal" ? OpenTerminalColorsButton : home.Children.OfType<SettingsCard>().FirstOrDefault(c => c.Tag as string == previous);
            card?.Focus(FocusState.Programmatic);
        }
        else SettingsBreadcrumb.Focus(FocusState.Programmatic);
        AnimateNavigation(target);
    }

    private void AnimateNavigation(FrameworkElement target)
    {
        _animatedPage = target;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var transform = new TranslateTransform();
        target.RenderTransform = transform;
        var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 56, To = 0, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = easing
        };
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160)
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, transform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "X");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        _navigationAnimation = new() { FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop };
        _navigationAnimation.Children.Add(slide);
        _navigationAnimation.Children.Add(fade);
        _navigationAnimation.Begin();
    }

    private void BuildColorFields(StackPanel terminalPanel, Grid ansiPanel, Dictionary<string, Button> fields, bool dark)
    {
        var ansiIndex = 0;
        foreach (var (key, label) in TerminalColors.Fields)
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            row.Children.Add(labelBlock);
            var button = new Button { MinWidth = 128, Padding = new Thickness(8, 4, 8, 4), HorizontalContentAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{(dark ? "深色" : "浅色")}终端 · {label}，选择颜色");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, $"{(dark ? "Dark" : "Light")}TerminalColor_{key}");
            button.Click += async (_, _) =>
            {
                if (_colorDialogOpen) return;
                _colorDialogOpen = true;
                try
                {
                    var current = button.Tag as string ?? TerminalColors.DefaultColor(key, dark);
                    var dialog = new ColorDialog($"{(dark ? "深色" : "浅色")}终端 · {label}", ColorDialog.Parse(current))
                    {
                        XamlRoot = XamlRoot,
                        RequestedTheme = ActualTheme
                    };
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.None) return;
                    if (result == ContentDialogResult.Primary) button.Tag = dialog.SelectedHex;
                    else if (result == ContentDialogResult.Secondary) button.Tag = null;
                    UpdateColorButton(button, key, dark);
                    SaveColors();
                }
                finally { _colorDialogOpen = false; }
            };
            fields.Add(key, button);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            if (key is "background" or "foreground" or "cursor" or "cursorAccent" or "selectionBackground")
                terminalPanel.Children.Add(row);
            else
            {
                if (ansiIndex < 8) ansiPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(row, ansiIndex % 8);
                Grid.SetColumn(row, ansiIndex / 8);
                ansiPanel.Children.Add(row);
                ansiIndex++;
            }
            UpdateColorButton(button, key, dark);
        }
    }

    private void ColorsLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0) return;
        var layout = (Grid)sender;
        var ansiSection = (StackPanel)layout.Children[1];
        var resetButton = (Button)layout.Children[2];
        var ansiPanel = (Grid)ansiSection.Children[1];
        var sideBySide = e.NewSize.Width >= 680;
        var ansiColumns = sideBySide || e.NewSize.Width >= 420 ? 2 : 1;
        layout.ColumnSpacing = sideBySide ? 24 : 0;
        layout.ColumnDefinitions[1].Width = sideBySide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        while (layout.RowDefinitions.Count < (sideBySide ? 2 : 3))
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (layout.RowDefinitions.Count > (sideBySide ? 2 : 3))
            layout.RowDefinitions.RemoveAt(layout.RowDefinitions.Count - 1);
        Grid.SetColumn(ansiSection, sideBySide ? 1 : 0);
        Grid.SetRow(ansiSection, sideBySide ? 0 : 1);
        Grid.SetRow(resetButton, sideBySide ? 1 : 2);
        ansiPanel.ColumnSpacing = ansiColumns == 2 ? 16 : 0;
        ansiPanel.ColumnDefinitions[1].Width = ansiColumns == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var ansiRows = 16 / ansiColumns;
        while (ansiPanel.RowDefinitions.Count < ansiRows)
            ansiPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (ansiPanel.RowDefinitions.Count > ansiRows)
            ansiPanel.RowDefinitions.RemoveAt(ansiPanel.RowDefinitions.Count - 1);
        for (var index = 0; index < ansiPanel.Children.Count; index++)
        {
            var row = (FrameworkElement)ansiPanel.Children[index];
            // Pair each normal ANSI color with its bright variant.
            Grid.SetColumn(row, ansiColumns == 2 ? index / 8 : 0);
            Grid.SetRow(row, ansiColumns == 2 ? index % 8 : index % 8 * 2 + index / 8);
        }
    }

    private static void UpdateColorButton(Button button, string key, bool dark)
    {
        var value = button.Tag as string;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(ColorDialog.Parse(value ?? TerminalColors.DefaultColor(key, dark))),
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleStrokeBrush"]
        });
        content.Children.Add(new TextBlock { Text = value ?? "默认", VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
    }

    private static void LoadColorFields(Dictionary<string, Button> fields, Dictionary<string, string> values, bool dark)
    {
        foreach (var (key, button) in fields)
        {
            button.Tag = values.GetValueOrDefault(key);
            UpdateColorButton(button, key, dark);
        }
    }

    private void SaveColors()
    {
        var colors = new TerminalColors
        {
            Light = _lightColors.Where(pair => pair.Value.Tag is string).ToDictionary(pair => pair.Key, pair => (string)pair.Value.Tag),
            Dark = _darkColors.Where(pair => pair.Value.Tag is string).ToDictionary(pair => pair.Key, pair => (string)pair.Value.Tag)
        };
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(TerminalColors: colors));
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        var dark = ((Button)sender).Tag as string == "dark";
        LoadColorFields(dark ? _darkColors : _lightColors, [], dark);
        SaveColors();
    }
    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(Theme: ThemeComboBox.SelectedIndex switch
        {
            1 => "浅色",
            2 => "深色",
            _ => "系统"
        }));
    }

    private void BackdropMaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(
            BackdropMaterial: BackdropMaterialComboBox.SelectedIndex == 1 ? "亚克力" : "Mica"));
    }

    private void TerminalFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value)) return;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(TerminalFontSize: sender.Value));
    }


    private async void ChooseDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        DownloadDirectoryBox.Text = folder.Path;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(DownloadDirectory: folder.Path));
    }

    private async void ClearLocalDataButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清除本地数据",
            Content = "这会删除所有已保存的服务器配置和已记录的主机指纹，远程服务器不会受到影响。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            // 一键清空全部本地数据不可恢复，Enter 默认落在取消上。
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ClearLocalDataRequested?.Invoke(this, EventArgs.Empty);
    }
}
