using System.Reflection;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Views.Session;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Syncfusion.UI.Xaml.DataGrid;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifyLocalFileMenuAsync()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FluentShell-local-menu-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture, "文件.txt"), "contents");
            Directory.CreateDirectory(Path.Combine(fixture, "文件夹"));
            await File.WriteAllBytesAsync(Path.Combine(fixture, "文件夹", "child"), [1, 2, 3]);
            _window = new Window { Title = "FluentShell offline local file menu regression" };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
            var root = (Grid)XamlReader.Load("""
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      Background="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
                """);
            var view = new SftpWorkspaceView(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            root.Children.Add(view);
            _window.Content = root;
            _window.Activate();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            async Task WaitFor(Func<bool> condition)
            {
                while (!condition()) await Task.Delay(20, timeout.Token);
            }
            void Check(bool passed, string message)
            {
                if (!passed) throw new InvalidOperationException(message);
                Program.Results.Add(message);
            }
            Task Call(string method, params object[] arguments) =>
                ((Task)typeof(SftpWorkspaceView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, arguments)!).WaitAsync(timeout.Token);
            await WaitFor(() => view.IsLoaded);
            await Call("NavigateLocalAsync", fixture);
            var local = (SfDataGrid)view.FindName("LocalFiles");
            SftpWorkspaceView.LocalPaneItem Item(string name) => ((System.Collections.IEnumerable)local.ItemsSource)
                .Cast<SftpWorkspaceView.LocalPaneItem>().Single(item => item.Name == name);
            var menu = (MenuFlyout)local.RecordContextFlyout;
            var empty = (MenuFlyout)typeof(SftpWorkspaceView).GetMethod("BuildLocalEmptyAreaMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
            MenuFlyoutItem Command(string label) => menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == label);
            async Task OpenMenu(MenuFlyout flyout)
            {
                flyout.ShowAt(local, new Windows.Foundation.Point(80, 100));
                await Task.Delay(120, timeout.Token);
            }
            async Task CloseMenu(MenuFlyout flyout)
            {
                flyout.Hide();
                await Task.Delay(150, timeout.Token);
            }
            void Select(params string[] names)
            {
                local.SelectedItems.Clear();
                foreach (var name in names) local.SelectedItems.Add(Item(name));
            }
            ContentDialog Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(popup => new[] { popup.Child }.Concat(Descendants(popup.Child))).OfType<ContentDialog>().Single();
            bool DialogOpen() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(popup => new[] { popup.Child }.Concat(Descendants(popup.Child))).OfType<ContentDialog>().Any();
            void Press(ContentDialog dialog, string name) =>
                ((IInvokeProvider)new ButtonAutomationPeer(Descendants(dialog).OfType<Button>().Single(button => button.Name == name))
                    .GetPattern(PatternInterface.Invoke)).Invoke();

            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                view.Render(new SftpSessionSnapshot(SftpSessionState.Idle, new SftpDirectoryListing("/", [
                    new RemoteFileItem { Name = "remote-folder", IsDirectory = true, FullPath = "/remote-folder" },
                    new RemoteFileItem { Name = "remote-file.txt", FullPath = "/remote-file.txt" }
                ]), true, true, false, "", null));
                await Task.Delay(120, timeout.Token);
                root.UpdateLayout();
                foreach (var table in new[] { local, (SfDataGrid)view.FindName("RemoteTable") })
                {
                    var icons = Descendants(table).OfType<FluentIcons.WinUI.SymbolIcon>().ToArray();
                    Check(icons.Any(icon => icon.Symbol == FluentIcons.Common.Symbol.Folder)
                        && icons.Any(icon => icon.Symbol == FluentIcons.Common.Symbol.Document)
                        && icons.All(icon => icon.ActualWidth == 16 && icon.ActualHeight == 16
                            && icon.FontSize == 16 && !string.IsNullOrEmpty(icon.Glyph)),
                        $"{theme}: {table.Name} renders distinct FluentIcons for folders and files at 16 DIPs");
                }
                await CaptureAsync(root, Program.ReportPath + $".{theme}.files.png");
                Select("文件.txt");
                await OpenMenu(menu);
                Check(Command("重命名").IsEnabled && Command("删除").IsEnabled && Command("属性").IsEnabled &&
                    !Command("上传").IsEnabled && Command("查看/编辑文本").IsEnabled, $"{theme}: local commands work while disconnected");
                Check(menu.Items.OfType<MenuFlyoutItem>().All(item => item.FontFamily.Source == "Microsoft YaHei UI"), $"{theme}: menu keeps Chinese font and icons");
                await CaptureAsync(VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Single().Child, Program.ReportPath + $".{theme}.menu.png");
                await CloseMenu(menu);

                Select("文件.txt", "文件夹");
                await OpenMenu(menu);
                Check(Command("删除").IsEnabled && Command("复制本地路径").IsEnabled && !Command("重命名").IsEnabled &&
                    !Command("属性").IsEnabled, $"{theme}: multiple selection has only appropriate commands " +
                    $"(selected={local.SelectedItems.Count}, delete={Command("删除").IsEnabled}, copy={Command("复制本地路径").IsEnabled}, rename={Command("重命名").IsEnabled}, properties={Command("属性").IsEnabled})");
                await CloseMenu(menu);
                Select("..");
                await OpenMenu(menu);
                Check(Command("打开文件夹").IsEnabled && Command("复制本地路径").IsEnabled && !Command("删除").IsEnabled &&
                    !Command("重命名").IsEnabled && !Command("属性").IsEnabled, $"{theme}: parent row cannot be modified");
                await CloseMenu(menu);
                await OpenMenu(empty);
                Check(empty.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[] { "刷新", "新建文件夹", "复制当前目录路径" }),
                    $"{theme}: blank-area menu has directory commands");
                await CloseMenu(empty);

                Select("文件夹");
                var properties = Call("ShowLocalPropertiesAsync");
                await WaitFor(DialogOpen);
                var dialog = Dialog();
                await WaitFor(() => Descendants((Grid)dialog.Content).OfType<TextBlock>().Any(text => text.Text.Contains("3 字节")));
                Check(dialog.ActualTheme == theme && Descendants((Grid)dialog.Content).OfType<TextBlock>().Any(text => text.Text == Path.Combine(fixture, "文件夹")),
                    $"{theme}: local properties show path and calculated folder size");
                await CaptureAsync(dialog, Program.ReportPath + $".{theme}.properties.png");
                dialog.Hide();
                await properties;
            }

            var create = Call("CreateLocalFolderAsync");
            await WaitFor(DialogOpen);
            var createDialog = Dialog();
            ((TextBox)createDialog.Content).Text = "新目录";
            Press(createDialog, "PrimaryButton");
            await create;
            Check(Directory.Exists(Path.Combine(fixture, "新目录")) && Item("新目录").IsDirectory, "Creating a folder updates local listing");
            Select("文件.txt");
            var rename = Call("RenameLocalItemAsync");
            await WaitFor(DialogOpen);
            var renameDialog = Dialog();
            Check(((TextBox)renameDialog.Content).Text == "文件.txt", "Rename prompt starts with selected filename");
            ((TextBox)renameDialog.Content).Text = "改名.txt";
            Press(renameDialog, "PrimaryButton");
            await rename;
            Check(File.ReadAllText(Path.Combine(fixture, "改名.txt")) == "contents" && Item("改名.txt").SizeBytes == 8, "Rename preserves contents and refreshes listing");
            Select("改名.txt");
            var delete = Call("DeleteLocalItemsAsync");
            await WaitFor(DialogOpen);
            Check(Dialog().DefaultButton == ContentDialogButton.Close && ((string)Dialog().Content).Contains("回收站"), "Delete defaults to cancellation and explains recycling");
            Dialog().Hide();
            await delete;
            Check(File.Exists(Path.Combine(fixture, "改名.txt")) && local.SelectedItems.Count == 1, "Cancelling deletion preserves file and selection");
            var pendingCreate = Call("CreateLocalFolderAsync");
            await WaitFor(DialogOpen);
            root.Children.Clear();
            root.UpdateLayout();
            await pendingCreate;
            Check(!DialogOpen(), "Unloading the pane closes its pending local dialog");
            _window.Close();
        }
        finally { Directory.Delete(fixture, recursive: true); }
        Program.Finish();
    }
}
