using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace FluentShell.Views.Shell;

/// <summary>Records the app's Ctrl+Shift+letter shortcuts without forwarding keys to SSH.</summary>
public sealed class ShortcutKeyPicker : Button
{
    private readonly ShortcutKeyPanel _panel = new();
    private string _key = "N";
    private bool _isOpen;
    private string _draft = "N";
    private ShortcutKeyPanel? _preview;
    private TextBlock? _prompt;
    internal static bool IsRecording { get; private set; }
    public string ActionTitle { get; set; } = "快捷键";
    public event EventHandler? ShortcutChanged;

    public string Key
    {
        get => _key;
        set
        {
            _key = value;
            _panel.SetKey(value);
            AutomationProperties.SetName(this, $"{ActionTitle}，Ctrl + Shift + {value}，点击修改");
        }
    }

    public ShortcutKeyPicker()
    {
        Content = _panel;
        Key = _key;
        Click += OpenPicker;
    }

    internal static bool IsSupported(VirtualKey key, VirtualKeyModifiers modifiers) =>
        key >= VirtualKey.A && key <= VirtualKey.Z &&
        modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);

    private void RecordKey(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (_preview is null || _prompt is null) return;
        if (!IsSupported(key, modifiers)) { _prompt.Text = "请使用 Ctrl + Shift + A–Z 组合键。"; return; }
        _draft = key.ToString();
        _preview.SetKey(_draft);
        _prompt.Text = "按“保存”应用此快捷键。";
    }

    private async void OpenPicker(object sender, RoutedEventArgs args)
    {
        if (_isOpen) return;
        _isOpen = true;
        IsRecording = true;
        try
        {
            _draft = Key;
            var preview = new ShortcutKeyPanel();
            _preview = preview;
            preview.SetKey(_draft);
            var prompt = new TextBlock { Text = "请按下 Ctrl + Shift + 字母", TextWrapping = TextWrapping.Wrap };
            _prompt = prompt;
            AutomationProperties.SetLiveSetting(prompt, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            var content = new StackPanel { Spacing = 16, MinWidth = 280 };
            content.Children.Add(prompt);
            content.Children.Add(preview);
            var dialog = new ContentDialog
            {
                Title = ActionTitle, Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = ActualTheme
            };
            dialog.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                if (e.Key is VirtualKey.Tab or VirtualKey.Escape or VirtualKey.Enter) return;
                e.Handled = true;
                if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
                var modifiers = VirtualKeyModifiers.None;
                bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
                if (Down(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
                if (Down(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
                if (Down(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
                if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
                RecordKey(e.Key, modifiers);
            }), true);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && _draft != Key)
            {
                Key = _draft;
                ShortcutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally { _preview = null; _prompt = null; IsRecording = false; _isOpen = false; Focus(FocusState.Programmatic); }
    }
}
