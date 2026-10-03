using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Views.Converters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluentShell.Views.Shell;

public sealed partial class SettingsPage
{
    private void ChangePreference(Func<UserPreferences, UserPreferences> change)
    {
        if (_loading) return;
        var next = change(_preferences).Normalize();
        if (!next.HasUniqueShortcuts)
        {
            ShortcutError.Text = "该快捷键已被其他操作使用，请选择不同字母。";
            ShortcutError.Visibility = Visibility.Visible;
            _loading = true;
            foreach (var load in _optionLoaders) load(_preferences);
            _loading = false;
            return;
        }
        ShortcutError.Visibility = Visibility.Collapsed;
        _preferences = next;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(Preferences: next));
    }

    private void Toggle(StackPanel panel, string title, string iconName, Func<UserPreferences, bool> get, Func<UserPreferences, bool, UserPreferences> set)
    {
        var toggle = new ToggleSwitch { OnContent = "开", OffContent = "关" };
        AutomationProperties.SetName(toggle, title);
        _optionLoaders.Add(p => toggle.IsOn = get(p));
        toggle.Toggled += (_, _) => ChangePreference(p => set(p, toggle.IsOn));
        panel.Children.Add(CreateOptionCard(title, iconName, toggle));
    }

    private void Number(StackPanel panel, string title, string iconName, int min, int max, Func<UserPreferences, int> get, Func<UserPreferences, int, UserPreferences> set)
    {
        var box = new NumberBox { Minimum = min, Maximum = max, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 160 };
        AutomationProperties.SetName(box, title);
        _optionLoaders.Add(p => box.Value = get(p));
        box.ValueChanged += (_, _) => { if (double.IsFinite(box.Value)) ChangePreference(p => set(p, (int)box.Value)); };
        panel.Children.Add(CreateOptionCard(title, iconName, box));
    }

    private void Choice(StackPanel panel, string title, string iconName, (string Value, string Label)[] options, Func<UserPreferences, string> get, Func<UserPreferences, string, UserPreferences> set)
    {
        var box = new ComboBox { MinWidth = 200 };
        AutomationProperties.SetName(box, title);
        foreach (var option in options) box.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        _optionLoaders.Add(p => box.SelectedIndex = Array.FindIndex(options, option => option.Value == get(p)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem item) ChangePreference(p => set(p, (string)item.Tag));
        };
        panel.Children.Add(CreateOptionCard(title, iconName, box));
    }

    private void Shortcut(string title, string iconName, Func<UserPreferences, string> get, Func<UserPreferences, string, UserPreferences> set)
    {
        var picker = new ShortcutKeyPicker { ActionTitle = title };
        _optionLoaders.Add(p => picker.Key = get(p));
        picker.ShortcutChanged += (_, _) => ChangePreference(p => set(p, picker.Key));
        ShortcutOptions.Children.Add(CreateOptionCard(title, iconName, picker));
    }

    private SettingsCard CreateOptionCard(string title, string iconName, UIElement editor) => new()
    {
        Header = title,
        HeaderIcon = new PathIcon
        {
            Data = IconGeometryConverter.Parse((string)Resources[$"Settings{iconName}IconData"]),
            Style = (Style)Resources["SettingsOptionIconStyle"]
        },
        Content = editor
    };

    private void BuildPreferenceEditors()
    {
        Toggle(AppearanceOptions, "侧栏默认展开", "Sidebar", p => p.SidebarOpen, (p, v) => p with { SidebarOpen = v });
        Choice(TerminalOptions, "终端字体", "Font", [("Cascadia Mono", "Cascadia Mono"), ("Consolas", "Consolas"), ("Courier New", "Courier New")], p => p.FontFamily, (p, v) => p with { FontFamily = v });
        Choice(TerminalOptions, "终端主题", "Theme", [("system", "跟随应用主题"), ("light", "固定浅色"), ("dark", "固定深色")], p => p.TerminalTheme, (p, v) => p with { TerminalTheme = v });
        Choice(TerminalOptions, "光标样式", "Cursor", [("bar", "竖线"), ("block", "方块"), ("underline", "下划线")], p => p.CursorStyle, (p, v) => p with { CursorStyle = v });
        Toggle(TerminalOptions, "光标闪烁", "Eye", p => p.CursorBlink, (p, v) => p with { CursorBlink = v });
        Number(TerminalOptions, "滚动缓冲行数（减少会丢弃较早输出）", "Scrollback", 1000, 100000, p => p.Scrollback, (p, v) => p with { Scrollback = v });
        Toggle(TerminalOptions, "选中即复制", "Copy", p => p.CopyOnSelect, (p, v) => p with { CopyOnSelect = v });
        Toggle(TerminalOptions, "右键粘贴", "Paste", p => p.RightClickPaste, (p, v) => p with { RightClickPaste = v });
        Toggle(TerminalOptions, "多行粘贴前确认", "Confirm", p => p.ConfirmMultilinePaste, (p, v) => p with { ConfirmMultilinePaste = v });
        Number(ConnectionOptions, "连接超时（秒）", "Timeout", 3, 120, p => p.ConnectionTimeoutSeconds, (p, v) => p with { ConnectionTimeoutSeconds = v });
        Number(ConnectionOptions, "保活间隔（秒，0 表示关闭）", "KeepAlive", 0, 300, p => p.KeepAliveSeconds, (p, v) => p with { KeepAliveSeconds = v });
        Number(ConnectionOptions, "断线自动重连次数（0 表示关闭）", "Reconnect", 0, 10, p => p.ReconnectAttempts, (p, v) => p with { ReconnectAttempts = v });
        Number(ConnectionOptions, "重连间隔（秒）", "Delay", 1, 60, p => p.ReconnectDelaySeconds, (p, v) => p with { ReconnectDelaySeconds = v });
        Toggle(TransferOptions, "下载时直接使用默认目录", "Download", p => p.UseDefaultDownloadDirectory, (p, v) => p with { UseDefaultDownloadDirectory = v });
        Toggle(TransferOptions, "显示隐藏文件（本地和远程）", "Eye", p => p.ShowHiddenFiles, (p, v) => p with { ShowHiddenFiles = v });
        Choice(TransferOptions, "文件已存在时", "Conflict", [("ask", "每批询问"), ("skip", "跳过"), ("overwrite", "覆盖（会替换已有文件）")], p => p.ConflictPolicy, (p, v) => p with { ConflictPolicy = v });
        Number(TransferOptions, "最大并发传输批次", "Concurrent", 1, 8, p => p.MaxTransfers, (p, v) => p with { MaxTransfers = v });
        Toggle(TransferOptions, "传输完成时显示应用内通知", "Notification", p => p.NotifyTransferComplete, (p, v) => p with { NotifyTransferComplete = v });
        Shortcut("新建会话", "NewSession", p => p.NewSessionKey, (p, v) => p with { NewSessionKey = v });
        Shortcut("关闭当前会话", "CloseSession", p => p.CloseSessionKey, (p, v) => p with { CloseSessionKey = v });
        Shortcut("切换到下一个会话", "NextSession", p => p.NextSessionKey, (p, v) => p with { NextSessionKey = v });
        Shortcut("查找终端内容", "Search", p => p.SearchTerminalKey, (p, v) => p with { SearchTerminalKey = v });
        Shortcut("展开或折叠文件管理器", "Files", p => p.ToggleFilesKey, (p, v) => p with { ToggleFilesKey = v });
    }
}
