using FluentShell.Models;
using FluentShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

public static class ShellDialogService
{
    public static async Task<string?> PromptSecretAsync(XamlRoot xamlRoot, ServerProfile profile)
    {
        var box = new PasswordBox
        {
            PlaceholderText = profile.Authentication == AuthenticationMethod.Password
                ? "输入密码"
                : "输入私钥口令（没有则留空）",
            PasswordRevealMode = PasswordRevealMode.Hidden
        };
        var dialog = new ContentDialog
        {
            Title = $"连接 {profile.Name}",
            Content = box,
            PrimaryButtonText = "连接",
            CloseButtonText = "取消",
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    public static async Task<bool> ConfirmFingerprintAsync(
        XamlRoot xamlRoot,
        HostFingerprintRequiredEventArgs fingerprint)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "这是此服务器第一次连接。请确认主机指纹与你信任的来源一致。",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = $"算法：{fingerprint.KeyType}\n指纹：{fingerprint.Fingerprint}",
            FontFamily = new FontFamily("Cascadia Mono"),
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            Title = "确认服务器指纹",
            Content = body,
            PrimaryButtonText = "信任并连接",
            CloseButtonText = "拒绝",
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    public static async Task<ServerProfile?> PickServerAsync(
        XamlRoot xamlRoot,
        IReadOnlyList<ServerProfile> profiles)
    {
        var list = new ListView
        {
            ItemsSource = profiles,
            DisplayMemberPath = nameof(ServerProfile.Name),
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth = 380,
            MaxHeight = 420
        };
        var dialog = new ContentDialog
        {
            Title = "打开服务器",
            Content = list,
            PrimaryButtonText = "连接",
            CloseButtonText = "取消",
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? list.SelectedItem as ServerProfile
            : null;
    }

    public static async Task<bool> ConfirmCloseSessionAsync(XamlRoot xamlRoot, string serverName)
    {
        var dialog = new ContentDialog
        {
            Title = "文件正在传输",
            Content = $"关闭“{serverName}”标签页会取消当前文件传输，是否继续？",
            PrimaryButtonText = "关闭标签页",
            CloseButtonText = "继续传输",
            XamlRoot = xamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}