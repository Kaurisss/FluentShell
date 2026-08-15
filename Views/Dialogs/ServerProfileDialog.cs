using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentShell.Views.Dialogs;

public sealed class ServerProfileDialogContext
{
    public required XamlRoot XamlRoot { get; init; }
    public required IntPtr WindowHandle { get; init; }
    public required Brush MutedTextBrush { get; init; }
    public required bool HasSavedCredential { get; init; }
    public required bool RememberCredentialsByDefault { get; init; }
}

public sealed class ServerProfileDialogResult
{
    public required ServerProfile Profile { get; init; }
    public required bool SaveCredential { get; init; }
    public required bool CredentialIdentityChanged { get; init; }
    public string OriginalUsername { get; init; } = string.Empty;
    public required bool ConnectAfterSave { get; init; }
    public string EnteredSecret { get; init; } = string.Empty;
}

public static class ServerProfileDialog
{
    public static async Task<ServerProfileDialogResult?> ShowAsync(
        ServerProfile? editing,
        ServerProfileDialogContext context)
    {
        var originalUsername = editing?.Username;
        var originalAuthentication = editing?.Authentication;
        var name = new TextBox
        {
            Header = "显示名称",
            Text = editing?.Name ?? string.Empty,
            PlaceholderText = "例如：生产服务器"
        };
        var host = new TextBox
        {
            Header = "主机地址",
            Text = editing?.Host ?? string.Empty,
            PlaceholderText = "example.com 或 IP 地址"
        };
        var port = new NumberBox
        {
            Header = "端口",
            Value = editing?.Port ?? 22,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var user = new TextBox
        {
            Header = "用户名",
            Text = editing?.Username ?? string.Empty
        };
        var authentication = new ComboBox
        {
            Header = "认证方式",
            SelectedIndex = editing?.Authentication == AuthenticationMethod.PrivateKey ? 1 : 0
        };
        authentication.Items.Add(new ComboBoxItem { Content = "密码" });
        authentication.Items.Add(new ComboBoxItem { Content = "私钥" });

        var secret = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek };
        var rememberCredential = new CheckBox
        {
            Content = "保存凭据到 Windows 凭据管理器",
            IsChecked = context.HasSavedCredential || context.RememberCredentialsByDefault
        };
        var credentialInfo = new TextBlock
        {
            Text = "凭据不会写入服务器配置文件。留空会保留已有凭据；取消勾选会删除这台服务器已保存的凭据。",
            FontSize = 12,
            Foreground = context.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        var keyPath = new TextBox
        {
            Header = "私钥文件",
            Text = editing?.PrivateKeyPath ?? string.Empty,
            PlaceholderText = "选择 OpenSSH 私钥文件",
            IsReadOnly = true
        };
        var keyPickerRow = BuildKeyPickerRow(keyPath, context.WindowHandle);

        void UpdateAuthenticationFields()
        {
            var usesPrivateKey = authentication.SelectedIndex == 1;
            var selectedAuthentication = usesPrivateKey
                ? AuthenticationMethod.PrivateKey
                : AuthenticationMethod.Password;
            var canPreserveSavedCredential = context.HasSavedCredential &&
                originalAuthentication == selectedAuthentication &&
                string.Equals(originalUsername, user.Text.Trim(), StringComparison.Ordinal);
            keyPickerRow.Visibility = usesPrivateKey ? Visibility.Visible : Visibility.Collapsed;
            secret.Header = usesPrivateKey ? "私钥口令（可选）" : "密码";
            secret.PlaceholderText = canPreserveSavedCredential
                ? "已保存；留空保持不变"
                : usesPrivateKey ? "私钥没有口令时可留空" : "输入登录密码";
        }

        authentication.SelectionChanged += (_, _) => UpdateAuthenticationFields();
        user.TextChanged += (_, _) => UpdateAuthenticationFields();
        UpdateAuthenticationFields();

        var notes = new TextBox
        {
            Header = "备注",
            Text = editing?.Notes ?? string.Empty,
            PlaceholderText = "可选",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        var form = new StackPanel { Spacing = 12, MaxWidth = 560 };
        foreach (var child in new UIElement[]
        {
            name,
            host,
            port,
            user,
            authentication,
            keyPickerRow,
            secret,
            rememberCredential,
            credentialInfo,
            notes
        })
        {
            form.Children.Add(child);
        }

        // 校验错误就地显示在表单里，而不是关掉对话框再弹提示——那样用户填的内容全丢了。
        var validationError = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        form.Children.Add(validationError);

        string? Validate()
        {
            if (string.IsNullOrWhiteSpace(name.Text) ||
                string.IsNullOrWhiteSpace(host.Text) ||
                string.IsNullOrWhiteSpace(user.Text))
            {
                return "显示名称、主机地址和用户名不能为空。";
            }
            if (authentication.SelectedIndex == 1 && string.IsNullOrWhiteSpace(keyPath.Text))
                return "私钥认证需要选择一个本机私钥文件。";
            return null;
        }

        void CancelCloseWhenInvalid(ContentDialog _, ContentDialogButtonClickEventArgs args)
        {
            var error = Validate();
            if (error is null) return;
            args.Cancel = true;
            validationError.Text = error;
            validationError.Visibility = Visibility.Visible;
        }

        var dialog = new ContentDialog
        {
            Title = editing is null ? "添加服务器" : "编辑服务器",
            Content = new ScrollViewer { Content = form, MaxHeight = 620 },
            PrimaryButtonText = editing is null ? "保存" : "保存修改",
            SecondaryButtonText = "保存并连接",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = context.XamlRoot
        };
        dialog.PrimaryButtonClick += CancelCloseWhenInvalid;
        dialog.SecondaryButtonClick += CancelCloseWhenInvalid;
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult == ContentDialogResult.None) return null;

        var selectedAuthentication = authentication.SelectedIndex == 1
            ? AuthenticationMethod.PrivateKey
            : AuthenticationMethod.Password;
        var newUsername = user.Text.Trim();
        var profile = editing ?? new ServerProfile();
        profile.Name = name.Text.Trim();
        profile.Host = host.Text.Trim();
        profile.Port = port.Value is double value && !double.IsNaN(value) ? (int)value : 22;
        profile.Username = newUsername;
        profile.Authentication = selectedAuthentication;
        profile.PrivateKeyPath = keyPath.Text.Trim();
        profile.Notes = notes.Text.Trim();

        return new ServerProfileDialogResult
        {
            Profile = profile,
            SaveCredential = rememberCredential.IsChecked == true,
            CredentialIdentityChanged = editing is not null &&
                (!string.Equals(originalUsername, newUsername, StringComparison.Ordinal) ||
                    originalAuthentication != selectedAuthentication),
            OriginalUsername = originalUsername ?? string.Empty,
            ConnectAfterSave = dialogResult == ContentDialogResult.Secondary,
            EnteredSecret = secret.Password
        };
    }

    private static Grid BuildKeyPickerRow(TextBox keyPath, IntPtr windowHandle)
    {
        var chooseKeyButton = new Button
        {
            Content = "选择文件",
            MinHeight = 40,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        chooseKeyButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) keyPath.Text = file.Path;
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(keyPath);
        Grid.SetColumn(chooseKeyButton, 1);
        row.Children.Add(chooseKeyButton);
        return row;
    }

}