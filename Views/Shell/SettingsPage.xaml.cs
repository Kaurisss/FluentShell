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
        BuildColorFields(LightColorsPanel, _lightColors, false);
        BuildColorFields(DarkColorsPanel, _darkColors, true);
        BuildPreferenceEditors();
        UpdateResponsiveLayout(30);
        SettingsBreadcrumb.ItemsSource = new[] { "设置" };
        VersionText.Text = Services.DiagnosticLog.Summary;
    }

    public void UpdateResponsiveLayout(double horizontalSpacing)
    {
        // Keep the scrollbar at the page edge; only the scrollable content is inset.
        var inset = new Thickness(0, 0, horizontalSpacing, 0);
        SettingsBreadcrumb.Margin = inset;
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

    private void BackToSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateCategory(null);
    }

    private void SettingsBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0) NavigateCategory(null);
    }

    private Dictionary<string, (ScrollViewer Page, string Title)> CategoryPages() => new()
    {
        ["appearance"] = (AppearancePage, "外观"), ["terminal"] = (TerminalColorsPage, "终端"),
        ["connection"] = (ConnectionPage, "连接"), ["transfer"] = (TransferSettingsPage, "文件传输"),
        ["shortcuts"] = (ShortcutSettingsPage, "快捷键"), ["data"] = (DataSettingsPage, "数据管理"),
        ["about"] = (AboutSettingsPage, "关于与诊断")
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
        AnimateNavigation(target, key is null);
    }

    private void AnimateNavigation(FrameworkElement target, bool back)
    {
        _animatedPage = target;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var transform = new TranslateTransform();
        target.RenderTransform = transform;
        var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = back ? -24 : 24, To = 0, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = easing
        };
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160)
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, transform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "X");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        _navigationAnimation = new();
        _navigationAnimation.Children.Add(slide);
        _navigationAnimation.Children.Add(fade);
        _navigationAnimation.Begin();
    }

    private void BuildColorFields(StackPanel panel, Dictionary<string, Button> fields, bool dark)
    {
        foreach (var (key, label) in TerminalColors.Fields)
        {
            var row = new SettingsCard { Header = label };
            var button = new Button { MinWidth = 180, HorizontalContentAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{(dark ? "深色" : "浅色")}终端 · {label}，选择颜色");
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
                    if (result == ContentDialogResult.Primary) button.Tag = dialog.SelectedHex;
                    else if (result == ContentDialogResult.Secondary) button.Tag = null;
                    UpdateColorButton(button, key, dark);
                }
                finally { _colorDialogOpen = false; }
            };
            fields.Add(key, button);
            row.Content = button;
            panel.Children.Add(row);
            UpdateColorButton(button, key, dark);
        }
    }

    private static void UpdateColorButton(Button button, string key, bool dark)
    {
        var value = button.Tag as string;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(4),
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

    private void SaveColors_Click(object sender, RoutedEventArgs e)
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
        LoadColorFields(_lightColors, [], false);
        LoadColorFields(_darkColors, [], true);
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(TerminalColors: new()));
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
