using FluentShell.Core;
using FluentShell.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentShell.Views.Shell;

public sealed partial class SettingsPage : UserControl
{
    private readonly IntPtr _windowHandle;
    private bool _loading;

    public SettingsPage(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        InitializeComponent();
    }

    public event EventHandler<AppSettingsUpdate>? SettingsChanged;
    public event EventHandler? ClearLocalDataRequested;

    public void SetSettings(AppSettings settings, string dataFolder)
    {
        _loading = true;
        ThemeComboBox.SelectedIndex = settings.Theme switch
        {
            "浅色" => 1,
            "深色" => 2,
            _ => 0
        };
        BackdropMaterialComboBox.SelectedIndex = settings.BackdropMaterial == "亚克力" ? 1 : 0;
        TerminalFontSizeBox.Value = settings.TerminalFontSize;
        DownloadDirectoryBox.Text = settings.DownloadDirectory;
        RememberCredentialsToggle.IsOn = settings.RememberCredentials;
        DataLocationText.Text = dataFolder;
        _loading = false;
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

    private void RememberCredentialsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(RememberCredentials: RememberCredentialsToggle.IsOn));
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