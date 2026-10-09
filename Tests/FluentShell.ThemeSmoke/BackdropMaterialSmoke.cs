using System.Reflection;
using FluentShell.Core;
using FluentShell.Services;
using FluentShell.Views.Shell;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifyBackdropMaterialsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FluentShell-backdrop-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = new MainWindow { Title = "FluentShell · 背景效果验证（离线）" };
            _window = window;
            // Exercise production settings events, redirecting their writes to an
            // isolated store before activation can load any saved user profiles.
            typeof(MainWindow).GetField("_loaded", flags)!.SetValue(window, true);
            var shell = Field<ShellCoordinator>(window, "_shell");
            typeof(ShellCoordinator).GetField("_localStore", flags)!.SetValue(shell, new LocalStore(folder));
            var root = (Grid)window.Content;
            var page = Field<SettingsPage>(window, "_settingsPage");
            var materialBox = (ComboBox)page.FindName("BackdropMaterialComboBox");
            var themeBox = (ComboBox)page.FindName("ThemeComboBox");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 1200;
                presenter.PreferredMinimumHeight = 750;
            }
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(20, 20));
            typeof(MainWindow).GetMethod("NavigateTo", flags)!.Invoke(window, ["settings", false]);
            typeof(SettingsPage).GetMethod("NavigateCategory", flags)!.Invoke(page, ["appearance"]);
            page.SetSettings(shell.Settings, folder);
            window.Activate();
            await Task.Delay(300, timeout.Token);

            void Check(bool passed, string description)
            {
                Program.Results.Add(new { control = description, passed });
                if (!passed) throw new InvalidOperationException(description);
            }
            async Task WaitFor(Func<bool> condition)
            {
                while (!condition()) await Task.Delay(25, timeout.Token);
            }
            bool Matches(string material) => material switch
            {
                "Mica" => window.SystemBackdrop is MicaBackdrop { Kind: MicaKind.Base },
                "Mica Alt" => window.SystemBackdrop is MicaBackdrop { Kind: MicaKind.BaseAlt },
                "亚克力" => window.SystemBackdrop is DesktopAcrylicBackdrop,
                "Acrylic Thin" => window.SystemBackdrop is ThinAcrylicBackdrop,
                _ => false
            };
            Check(materialBox.Items.Count == 4, "Settings exposes all four backdrop materials");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                themeBox.SelectedIndex = theme == ElementTheme.Light ? 1 : 2;
                await WaitFor(() => root.RequestedTheme == theme);
                foreach (var material in new[] { "Mica", "Mica Alt", "亚克力", "Acrylic Thin", "Mica" })
                {
                    var previousThin = window.SystemBackdrop as ThinAcrylicBackdrop;
                    var previousController = previousThin is null ? null : Field<DesktopAcrylicController?>(previousThin, "_controller");
                    materialBox.SelectedItem = materialBox.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == material);
                    await WaitFor(() => shell.Settings.BackdropMaterial == material && Matches(material));
                    Check((await new SettingsStore(folder).LoadAsync()).BackdropMaterial == material,
                        $"{theme}/{material}: selecting the option applies and persists the native material");
                    if (previousController is not null)
                        Check(previousController.IsClosed && Field<DesktopAcrylicController?>(previousThin!, "_controller") is null,
                            "Switching away from Acrylic Thin disposes its controller");
                    if (window.SystemBackdrop is ThinAcrylicBackdrop thin)
                    {
                        var controller = Field<DesktopAcrylicController>(thin, "_controller");
                        Check(controller.Kind == DesktopAcrylicKind.Thin && !controller.IsClosed, "Acrylic Thin uses the live native Thin variant");
                        var tint = controller.TintColor;
                        root.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
                        await WaitFor(() => controller.TintColor != tint);
                        Check(ReferenceEquals(window.SystemBackdrop, thin) && !controller.IsClosed,
                            "The existing Thin controller follows default configuration changes without replacement");
                        root.RequestedTheme = theme;
                        await WaitFor(() => controller.TintColor == tint);
                        themeBox.SelectedIndex = theme == ElementTheme.Light ? 2 : 1;
                        await WaitFor(() => root.RequestedTheme != theme && controller.IsClosed);
                        // Applying settings replaces the backdrop; the new instance
                        // must inherit the updated default system configuration.
                        var changed = Field<DesktopAcrylicController>((ThinAcrylicBackdrop)window.SystemBackdrop, "_controller");
                        Check(changed.TintColor != tint && changed.Kind == DesktopAcrylicKind.Thin,
                            "Acrylic Thin follows the selected theme and releases its previous controller");
                        themeBox.SelectedIndex = theme == ElementTheme.Light ? 1 : 2;
                        await WaitFor(() => root.RequestedTheme == theme && changed.IsClosed);
                    }
                    page.SetSettings(await new SettingsStore(folder).LoadAsync(), folder);
                    Check(((ComboBoxItem)materialBox.SelectedItem).Tag as string == material, "Reloading settings restores the selected material");
                    await Task.Delay(150, timeout.Token);
                    await BackdropUiAsync(hwnd, "screenshot", "--output", Program.ReportPath + $".{theme}.{material.Replace(' ', '-')}.png");
                }
            }
            window.Close();
        }
        finally { Directory.Delete(folder, recursive: true); }
        Program.Finish();
    }
}
