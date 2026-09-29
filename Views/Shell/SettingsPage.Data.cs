using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace FluentShell.Views.Shell;

public sealed partial class SettingsPage
{
    private bool _dataOperation;

    private async Task RunDataOperationAsync(Func<Task> action)
    {
        if (_dataOperation) return;
        _dataOperation = true;
        try { await action(); }
        catch (Exception)
        {
            DiagnosticLog.Record("SettingsDataOperationFailed");
            await ShellDialogService.ShowMessageAsync(XamlRoot, "操作未完成", "请检查文件格式、目录权限或文件是否被其他程序占用。");
        }
        finally { _dataOperation = false; }
    }

    private async Task<bool> ConfirmSettingsAsync(string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "确认更改设置", Content = message,
            PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void ExportSettings_Click(object sender, RoutedEventArgs e) => await RunDataOperationAsync(async () =>
    {
        var picker = new FileSavePicker { SuggestedFileName = "FluentShell-settings" };
        picker.FileTypeChoices.Add("JSON 设置", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is not null) await FileIO.WriteTextAsync(file, SettingsBackup.Export(_currentSettings));
    });

    private async void ImportSettings_Click(object sender, RoutedEventArgs e) => await RunDataOperationAsync(async () =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if ((await file.GetBasicPropertiesAsync()).Size > 1_000_000) throw new InvalidDataException();
        var settings = SettingsBackup.Import(await FileIO.ReadTextAsync(file));
        if (!await ConfirmSettingsAsync("将替换当前应用设置并应用到已有会话。服务器配置、主机指纹和凭据保持不变。")) return;
        SettingsChanged?.Invoke(this, new AppSettingsUpdate(Replacement: settings));
    });

    private async void ResetSettings_Click(object sender, RoutedEventArgs e) => await RunDataOperationAsync(async () =>
    {
        if (await ConfirmSettingsAsync("恢复所有应用设置的默认值。服务器配置、主机指纹和凭据保持不变。"))
            SettingsChanged?.Invoke(this, new AppSettingsUpdate(Replacement: new AppSettings()));
    });

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e) => await RunDataOperationAsync(async () =>
    {
        Directory.CreateDirectory(DataLocationText.Text);
        if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(DataLocationText.Text))) throw new IOException();
    });

    private async void OpenLogsFolder_Click(object sender, RoutedEventArgs e) => await RunDataOperationAsync(async () =>
    {
        Directory.CreateDirectory(DiagnosticLog.Folder);
        if (!await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(DiagnosticLog.Folder))) throw new IOException();
    });

    private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e) => await RunDataOperationAsync(() =>
    {
        var data = new DataPackage();
        data.SetText(DiagnosticLog.Summary);
        Clipboard.SetContent(data);
        return Task.CompletedTask;
    });
}
