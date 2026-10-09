using FluentShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FluentShell.Views.Session;

public sealed partial class SftpWorkspaceView
{
    private IReadOnlyList<LocalPaneItem> GetLocalSelection()
    {
        var selected = LocalFiles.SelectedItems.Cast<LocalPaneItem>().ToArray();
        return selected.Any(item => item.Name == "..") ? [] : selected;
    }

    private MenuFlyout BuildLocalEmptyAreaMenu()
    {
        var menu = new MenuFlyout();
        var refresh = new MenuFlyoutItem { Text = "刷新", Icon = CreateMenuIcon("Refresh") };
        refresh.Click += (_, _) => RefreshLocalDirectory();
        menu.Items.Add(refresh);
        menu.Items.Add(new MenuFlyoutSeparator());
        var newFolder = new MenuFlyoutItem { Text = "新建文件夹", Icon = CreateMenuIcon("FolderAdd") };
        newFolder.Click += async (_, _) => await CreateLocalFolderAsync();
        menu.Items.Add(newFolder);
        var copy = new MenuFlyoutItem { Text = "复制当前目录路径", Icon = CreateMenuIcon("Copy") };
        copy.Click += (_, _) => { if (_localPath is { } path) CopyLocalPaths([path]); };
        menu.Items.Add(copy);
        menu.Opened += (_, _) =>
        {
            refresh.IsEnabled = LocalFiles.IsEnabled && !_localOperationBusy;
            newFolder.IsEnabled = refresh.IsEnabled && _localPath is not null;
            copy.IsEnabled = _localPath is not null;
        };
        ApplyChineseMenuFont(menu);
        return menu;
    }

    private void CopyLocalPaths(IEnumerable<string> paths)
    {
        var text = string.Join(Environment.NewLine, paths);
        if (text.Length == 0) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception exception) { _ = ShowFailureDialogAsync($"复制本地路径失败：{exception.Message}"); }
    }

    private async Task<ContentDialogResult> ShowLocalOperationDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = ActualTheme;
        _localOperationDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { _localOperationDialog = null; }
    }

    private async Task<string?> PromptLocalNameAsync(string title, string initialName = "")
    {
        var box = new TextBox { PlaceholderText = "输入名称", Text = initialName, SelectionStart = 0, SelectionLength = initialName.Length };
        var dialog = new ContentDialog
        {
            Title = title, Content = box, PrimaryButtonText = "确定", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await ShowLocalOperationDialogAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    }

    private Task CreateLocalFolderAsync() => RunLocalOperationAsync(async directory =>
    {
        var name = await PromptLocalNameAsync("新建本地文件夹");
        if (name is null || !_localLoaded) return false;
        await _localFileService.CreateDirectoryAsync(directory, name);
        return true;
    });

    private Task RenameLocalItemAsync()
    {
        var selected = GetLocalSelection();
        if (selected.Count != 1) return Task.CompletedTask;
        var item = selected[0];
        return RunLocalOperationAsync(async directory =>
        {
            var name = await PromptLocalNameAsync(item.IsDirectory ? "重命名本地文件夹" : "重命名本地文件", item.Name);
            if (name is null || name == item.Name || !_localLoaded) return false;
            await _localFileService.RenameAsync(directory, item.Name, name);
            return true;
        });
    }

    private Task DeleteLocalItemsAsync()
    {
        var selected = GetLocalSelection();
        if (selected.Count == 0 || selected.Any(item => item.IsLink)) return Task.CompletedTask;
        var names = selected.Select(item => item.Name).ToArray();
        return RunLocalOperationAsync(async directory =>
        {
            var dialog = new ContentDialog
            {
                Title = "删除本地文件",
                Content = names.Length == 1
                    ? (selected[0].IsDirectory ? $"删除文件夹“{names[0]}”及其内容吗？" : $"删除文件“{names[0]}”吗？") +
                      "\n默认移入回收站；若系统设置或当前位置不支持回收站，文件可能被永久删除。"
                    : $"删除选中的 {names.Length} 个文件或文件夹吗？\n默认移入回收站；若系统设置或当前位置不支持回收站，文件可能被永久删除。",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await ShowLocalOperationDialogAsync(dialog) != ContentDialogResult.Primary || !_localLoaded) return false;
            await _localFileService.RecycleAsync(directory, names);
            return true;
        });
    }

    private async Task RunLocalOperationAsync(Func<string, Task<bool>> operation)
    {
        if (!_localLoaded || _localPath is not { } directory || !LocalFiles.IsEnabled || _localOperationBusy || XamlRoot is null) return;
        var version = _localReadVersion;
        var refresh = false;
        _localOperationBusy = true;
        LocalFiles.IsEnabled = false;
        LocalPathBox.IsEnabled = false;
        try { refresh = await operation(directory); }
        catch (OperationCanceledException) { refresh = true; }
        catch (Exception exception)
        {
            refresh = true;
            if (_localLoaded) await ShowFailureDialogAsync($"本地文件操作失败：{exception.Message}");
        }
        finally
        {
            _localOperationBusy = false;
            LocalPathBox.IsEnabled = true;
            if (_localLoaded)
            {
                if (refresh || version != _localReadVersion) await NavigateLocalAsync(_localPath ?? directory);
                else LocalFiles.IsEnabled = true;
            }
        }
    }

    private async Task ShowLocalPropertiesAsync()
    {
        var selected = GetLocalSelection();
        if (selected.Count != 1 || XamlRoot is null || _propertiesDialog is not null) return;
        var item = selected[0];
        using var cancellation = new CancellationTokenSource();
        var panel = new Grid { ColumnSpacing = 16, RowSpacing = 8, MinWidth = 360 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock Row(string label, string value)
        {
            var row = panel.RowDefinitions.Count;
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(caption, row); Grid.SetRow(text, row); Grid.SetColumn(text, 1);
            panel.Children.Add(caption); panel.Children.Add(text);
            return text;
        }
        Row("名称", item.Name);
        Row("类型", item.IsLink ? $"{item.TypeLabel}（链接）" : item.TypeLabel);
        Row("本地路径", item.FullPath);
        var size = Row("大小", item.IsDirectory ? "正在计算…" : $"{item.SizeLabel}（{item.SizeBytes:N0} 字节）");
        Row("修改时间", item.ModifiedLabel);
        var dialog = new ContentDialog
        {
            Title = $"“{item.Name}”属性", Content = panel, CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close, RequestedTheme = ActualTheme, XamlRoot = XamlRoot
        };
        _propertiesDialog = dialog;
        _directorySizeCancellation = cancellation;
        dialog.Closed += (_, _) => cancellation.Cancel();
        if (item.IsDirectory) dialog.Opened += (_, _) => _ = UpdateLocalDirectorySizeAsync(item.FullPath, size, cancellation.Token);
        try { await dialog.ShowAsync(); }
        finally { cancellation.Cancel(); _directorySizeCancellation = null; _propertiesDialog = null; }
    }

    private async Task UpdateLocalDirectorySizeAsync(string path, TextBlock size, CancellationToken token)
    {
        try
        {
            var bytes = await _localFileService.GetDirectorySizeAsync(path, token);
            if (!token.IsCancellationRequested)
                size.Text = bytes is { } value ? $"{SftpFileService.FormatSize(value)}（{value:N0} 字节）" : "不统计链接目标";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!token.IsCancellationRequested) size.Text = $"计算失败：{exception.Message}"; }
    }
}
