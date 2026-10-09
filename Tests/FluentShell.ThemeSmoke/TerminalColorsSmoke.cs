using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Dialogs;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifyTerminalColorsAsync()
    {
        var fixtureFolder = Path.Combine(Path.GetTempPath(), "FluentShell-terminal-colors-" + Guid.NewGuid());
        Directory.CreateDirectory(fixtureFolder);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            _window = new Window { Title = "FluentShell terminal color settings (offline)" };
            var root = new Grid { Width = 1000, Height = 900, RequestedTheme = ElementTheme.Light };
            _window.Content = root;
            var page = new SettingsPage(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var settings = new AppSettings
            {
                TerminalColors = new()
                {
                    Light = new() { ["red"] = "#912345" },
                    Dark = new() { ["background"] = "#234567", ["red"] = "#A12345" }
                }
            };
            var store = new SettingsStore(fixtureFolder);
            page.SetSettings(settings, fixtureFolder);
            root.Children.Add(page);
            _window.Activate();
            await Task.Delay(250, timeout.Token);
            var scale = root.XamlRoot.RasterizationScale;
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(1000 * scale)));
            typeof(SettingsPage).GetMethod("NavigateCategory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(page, ["terminal"]);
            await Task.Delay(300, timeout.Token);
            var light = Field<Dictionary<string, Button>>(page, "_lightColors");
            var dark = Field<Dictionary<string, Button>>(page, "_darkColors");
            var updates = 0;
            Task saved = Task.CompletedTask;
            page.SettingsChanged += (_, change) =>
            {
                if (change.TerminalColors is null) return;
                updates++;
                settings.TerminalColors = change.TerminalColors.Normalize();
                page.SetSettings(settings, fixtureFolder);
                saved = store.SaveAsync(settings);
            };

            void Check(bool passed, string description)
            {
                Program.Results.Add(new { control = description, passed });
                if (!passed) throw new InvalidOperationException(description);
            }
            void InvokeButton(Button button) =>
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            async Task ChooseColor(Button button, string hex, string? action)
            {
                button.StartBringIntoView();
                await Task.Delay(100, timeout.Token);
                InvokeButton(button);
                await Task.Delay(200, timeout.Token);
                var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                    .SelectMany(popup => new[] { popup.Child }.Concat(Descendants(popup.Child))).OfType<ColorDialog>().Single();
                Check(dialog.XamlRoot == page.XamlRoot && dialog.RequestedTheme == page.ActualTheme,
                    "Color picker uses its owning window and theme");
                Check(dialog.PrimaryButtonText == "保存", "Color picker exposes the save action");
                ((ColorPicker)((ScrollViewer)dialog.Content).Content).Color = ColorDialog.Parse(hex);
                if (action is null) dialog.Hide();
                else InvokeButton(Descendants(dialog).OfType<Button>().Single(candidate => candidate.Name == action));
                while (Field<bool>(page, "_colorDialogOpen")) await Task.Delay(25, timeout.Token);
                await saved;
            }

            ((Expander)page.FindName("LightColorsExpander")).IsExpanded = true;
            await ChooseColor(light["background"], "#123456", null);
            Check(updates == 0 && light["background"].Tag is null && !File.Exists(Path.Combine(fixtureFolder, "settings.json")),
                "Cancel leaves the color and persisted settings unchanged");
            await ChooseColor(light["background"], "#123456", "PrimaryButton");
            var persisted = await store.LoadAsync();
            Check(updates == 1 && light["background"].Tag as string == "#123456"
                && persisted.TerminalColors.Light["background"] == "#123456"
                && persisted.TerminalColors.Light["red"] == "#912345"
                && persisted.TerminalColors.Dark["background"] == "#234567",
                "Picker save immediately persists the color and preserves both palettes");
            await ChooseColor(light["background"], "#654321", null);
            Check(updates == 1 && light["background"].Tag as string == "#123456",
                "Cancel after editing retains the previously saved override");
            await ChooseColor(light["background"], "#654321", "SecondaryButton");
            persisted = await store.LoadAsync();
            Check(updates == 2 && !persisted.TerminalColors.Light.ContainsKey("background")
                && persisted.TerminalColors.Light["red"] == "#912345"
                && persisted.TerminalColors.Dark["background"] == "#234567",
                "Use default immediately saves only the selected color reset");

            root.RequestedTheme = ElementTheme.Dark;
            ((Expander)page.FindName("DarkColorsExpander")).IsExpanded = true;
            await Task.Delay(100, timeout.Token);
            await ChooseColor(dark["foreground"], "#ABCDEF", "PrimaryButton");
            persisted = await store.LoadAsync();
            Check(updates == 3 && persisted.TerminalColors.Dark["foreground"] == "#ABCDEF"
                && persisted.TerminalColors.Light["red"] == "#912345", "Dark picker save preserves light overrides");
            InvokeButton((Button)page.FindName("ResetLightColorsButton"));
            await saved;
            persisted = await store.LoadAsync();
            Check(updates == 4 && persisted.TerminalColors.Light.Count == 0
                && persisted.TerminalColors.Dark["background"] == "#234567"
                && persisted.TerminalColors.Dark["foreground"] == "#ABCDEF"
                && light.Values.All(button => button.Tag is null), "Light reset preserves every dark override");

            root.RequestedTheme = ElementTheme.Light;
            await Task.Delay(100, timeout.Token);
            await ChooseColor(light["blue"], "#13579B", "PrimaryButton");
            InvokeButton((Button)page.FindName("ResetDarkColorsButton"));
            await saved;
            persisted = await store.LoadAsync();
            Check(updates == 6 && persisted.TerminalColors.Dark.Count == 0
                && persisted.TerminalColors.Light["blue"] == "#13579B"
                && dark.Values.All(button => button.Tag is null), "Dark reset preserves every light override");
            var reloadedPage = new SettingsPage(WinRT.Interop.WindowNative.GetWindowHandle(_window));
            reloadedPage.SetSettings(persisted, fixtureFolder);
            Check(Field<Dictionary<string, Button>>(reloadedPage, "_lightColors")["blue"].Tag as string == "#13579B",
                "A new settings page loads the saved color without a page save button");

            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                foreach (var height in new[] { 420d, 700d })
                {
                    var dialog = new ColorDialog("终端 · 背景", ColorDialog.Parse("#123456"))
                    {
                        XamlRoot = root.XamlRoot,
                        RequestedTheme = theme
                    };
                    dialog.Resources["ContentDialogMaxHeight"] = height;
                    var showing = dialog.ShowAsync().AsTask();
                    await Task.Delay(250, timeout.Token);
                    var scroller = (ScrollViewer)dialog.Content;
                    scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                    root.UpdateLayout();
                    var surface = Descendants(dialog).OfType<Border>().Single(border => border.Name == "BackgroundElement");
                    var bar = Descendants(scroller).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                        .First(candidate =>
                        {
                            if (candidate.Orientation != Orientation.Vertical) return false;
                            DependencyObject? parent = candidate;
                            do { parent = VisualTreeHelper.GetParent(parent); } while (parent is not null && parent is not ScrollViewer);
                            return ReferenceEquals(parent, scroller);
                        });
                    var barBounds = bar.TransformToVisual(surface).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
                    var picker = (ColorPicker)scroller.Content;
                    var pickerBounds = picker.TransformToVisual(surface).TransformBounds(new Rect(0, 0, picker.ActualWidth, picker.ActualHeight));
                    Program.Results.Add(new { control = "color dialog scrollbar bounds", theme = theme.ToString(), height,
                        surfaceWidth = surface.ActualWidth, barBounds, pickerBounds, scrollerWidth = scroller.ActualWidth, scroller.ScrollableHeight });
                    Check(Math.Abs(barBounds.Right - (surface.ActualWidth - surface.BorderThickness.Right)) <= 2,
                        $"{theme}/{height}: color dialog scrollbar reaches its right edge");
                    Check(pickerBounds.Right <= barBounds.X && pickerBounds.X >= 20,
                        $"{theme}/{height}: picker retains its inset without scrollbar overlap");
                    if (height == 420)
                    {
                        Check(scroller.ScrollableHeight > 0, $"{theme}: short color dialog allows vertical scrolling");
                        scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
                        await Task.Delay(100, timeout.Token);
                        Check(Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) < 1,
                            $"{theme}: short color dialog can reach its last color input");
                        await CaptureAsync(surface, Program.ReportPath + $".picker.{theme}.{height}.bottom.png");
                        scroller.ChangeView(null, 0, null, true);
                        await Task.Delay(100, timeout.Token);
                    }
                    await CaptureAsync(surface, Program.ReportPath + $".picker.{theme}.{height}.png");
                    Program.Results.Add(new { control = "color dialog scrollbar layout", theme = theme.ToString(), height,
                        surfaceWidth = surface.ActualWidth, barBounds, pickerBounds, scroller.ScrollableHeight, passed = true });
                    dialog.Hide();
                    await showing.WaitAsync(timeout.Token);
                }
            }

            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                root.Background = new SolidColorBrush(theme == ElementTheme.Light
                    ? Microsoft.UI.Colors.WhiteSmoke : ColorDialog.Parse("#202020"));
                foreach (var width in new[] { 1000d, 760d, 600d, 390d })
                {
                    root.Width = width;
                    await Task.Delay(100, timeout.Token);
                    root.UpdateLayout();
                    foreach (var palette in new[] { "Light", "Dark" })
                    {
                        var expander = (Expander)page.FindName(palette + "ColorsExpander");
                        var layout = (Grid)page.FindName(palette + "ColorsLayout");
                        var terminalPanel = (StackPanel)page.FindName(palette + "TerminalColorsPanel");
                        var ansiPanel = (Grid)page.FindName(palette + "AnsiColorsPanel");
                        var reset = (Button)page.FindName("Reset" + palette + "ColorsButton");
                        expander.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                        await Task.Delay(100, timeout.Token);
                        root.UpdateLayout();
                        var sideBySide = layout.ActualWidth >= 680;
                        var terminalBounds = terminalPanel.TransformToVisual(layout).TransformBounds(new Rect(0, 0, terminalPanel.ActualWidth, terminalPanel.ActualHeight));
                        var ansiBounds = ansiPanel.TransformToVisual(layout).TransformBounds(new Rect(0, 0, ansiPanel.ActualWidth, ansiPanel.ActualHeight));
                        Check(terminalPanel.Children.Count == 5 && ansiPanel.Children.Count == 16,
                            $"{theme}/{width}/{palette}: all terminal and ANSI fields are present");
                        Check(sideBySide ? ansiBounds.X >= terminalBounds.Right && Math.Abs(ansiBounds.Y - terminalBounds.Y) < 1
                            : ansiBounds.Y >= terminalBounds.Bottom,
                            $"{theme}/{width}/{palette}: groups adapt without overlap");
                        Check(ReferenceEquals(expander.Content, layout) && ReferenceEquals(reset.Parent, layout),
                            $"{theme}/{width}/{palette}: reset belongs to its palette expander");
                        if (sideBySide) Check(layout.ActualHeight < 450, $"{theme}/{width}/{palette}: expanded editor remains compact");
                        foreach (var button in (palette == "Light" ? light : dark).Values)
                        {
                            var bounds = button.TransformToVisual(layout).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                            Check(bounds.X >= -1 && bounds.Right <= layout.ActualWidth + 1 && button.ActualWidth >= 128,
                                $"{theme}/{width}: {button.GetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty)} fits the editor");
                        }
                        Check((palette == "Light" ? light : dark)["background"].Focus(FocusState.Keyboard),
                            $"{theme}/{width}/{palette}: color fields accept keyboard focus");
                        // Capture the owning surface so translucent dark controls have their real backdrop.
                        await CaptureAsync(root, Program.ReportPath + $".{theme}.{width}.{palette}.png");
                        Program.Results.Add(new { control = "terminal color layout", theme = theme.ToString(), width, palette,
                            editorWidth = layout.ActualWidth, editorHeight = layout.ActualHeight, sideBySide, terminalBounds, ansiBounds, passed = true });
                    }
                }
            }
        }
        finally { Directory.Delete(fixtureFolder, true); }
        Program.Finish();
    }
}
