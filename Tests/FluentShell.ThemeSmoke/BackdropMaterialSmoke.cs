using System.Reflection;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Session;
using FluentShell.Views.Shell;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEditor;

namespace FluentShell.ThemeSmoke;

internal sealed partial class SmokeApp
{
    private async Task VerifyBackdropMaterialsAsync()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, diagnostic) =>
        {
            if (diagnostic.Exception is ArgumentException && diagnostic.Exception.Message.Contains("target"))
                Program.Results.Add(new { control = "Backdrop target diagnostic", error = diagnostic.Exception.ToString(), stack = Environment.StackTrace });
        };
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
            bool Matches(Window target, string material) => material switch
            {
                "Mica" => target.SystemBackdrop is MicaBackdrop { Kind: MicaKind.Base },
                "Mica Alt" => target.SystemBackdrop is MicaBackdrop { Kind: MicaKind.BaseAlt },
                "亚克力" => target.SystemBackdrop is DesktopAcrylicBackdrop,
                "Acrylic Thin" => target.SystemBackdrop is ThinAcrylicBackdrop,
                _ => false
            };
            Check(materialBox.Items.Count == 4, "Settings exposes all four backdrop materials");
            materialBox.SelectedIndex = 1;
            await WaitFor(() => shell.Settings.BackdropMaterial == "Mica Alt" && Matches(window, "Mica Alt"));
            var localPath = Path.Combine(folder, "editor-local.txt");
            await File.WriteAllTextAsync(localPath, "original local text");
            var localEditor = new TextFileEditorWindow(new LocalTextFileService(), localPath, "editor-local.txt", false, root.XamlRoot, hwnd);
            var remoteService = new SyntheticTextService();
            var remoteEditor = new TextFileEditorWindow(remoteService, "/editor-remote.txt", "editor-remote.txt", true, root.XamlRoot, hwnd);
            var editors = new[] { localEditor, remoteEditor };
            foreach (var editor in editors) editor.Activate();
            await WaitFor(() => editors.All(editor => !EditorElement<ProgressRing>(editor, "LoadingRing").IsActive));
            Check(editors.All(editor => Matches(editor, "Mica Alt")), "New local and remote editors inherit the selected Mica Alt material");
            foreach (var editor in editors)
            {
                EditorElement<CheckBox>(editor, "ReadOnlyBox").IsChecked = false;
                ReplaceDocument(EditorElement<CodeEditorControl>(editor, "CodeEditor"), "unsaved backdrop draft");
            }
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                themeBox.SelectedIndex = theme == ElementTheme.Light ? 1 : 2;
                await WaitFor(() => root.ActualTheme == theme && editors.All(editor => ((Grid)editor.Content).ActualTheme == theme));
                foreach (var material in new[] { "Mica", "Mica Alt", "亚克力", "Mica" })
                {
                    var previousThin = window.SystemBackdrop as ThinAcrylicBackdrop;
                    var previousController = previousThin is null ? null : Field<DesktopAcrylicController?>(previousThin, "_controller");
                    var previousEditorControllers = editors.Select(editor => editor.SystemBackdrop is ThinAcrylicBackdrop thinEditor
                        ? Field<DesktopAcrylicController?>(thinEditor, "_controller") : null).ToArray();
                    materialBox.SelectedItem = materialBox.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == material);
                    await WaitFor(() => shell.Settings.BackdropMaterial == material && Matches(window, material) &&
                        editors.All(editor => Matches(editor, material) && ((Grid)editor.Content).ActualTheme == theme));
                    Check((await new SettingsStore(folder).LoadAsync()).BackdropMaterial == material,
                        $"{theme}/{material}: selecting the option applies and persists the native material");
                    Check(editors.All(editor => ((Grid)editor.Content).ActualTheme == theme &&
                        !ReferenceEquals(editor.SystemBackdrop, window.SystemBackdrop)) && !ReferenceEquals(localEditor.SystemBackdrop, remoteEditor.SystemBackdrop),
                        $"{theme}/{material}: open local and remote editors follow settings with independent backdrops");
                    Check(editors.All(editor => EditorElement<CodeEditorControl>(editor, "CodeEditor").Editor.Modify &&
                        EditorElement<CodeEditorControl>(editor, "CodeEditor").Editor.GetText(100).TrimEnd('\0') == "unsaved backdrop draft"),
                        "Changing appearance preserves both unsaved editor drafts");
                    Check(previousEditorControllers.All(controller => controller is null || controller.IsClosed),
                        "Editor Acrylic Thin controllers are disposed on material changes");
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
                        await WaitFor(() => root.RequestedTheme != theme && controller.TintColor != tint);
                        Check(ReferenceEquals(window.SystemBackdrop, thin) && !controller.IsClosed,
                            "Acrylic Thin follows the selected theme without replacing its controller");
                        themeBox.SelectedIndex = theme == ElementTheme.Light ? 1 : 2;
                        await WaitFor(() => root.RequestedTheme == theme && controller.TintColor == tint);
                    }
                    page.SetSettings(await new SettingsStore(folder).LoadAsync(), folder);
                    Check(((ComboBoxItem)materialBox.SelectedItem).Tag as string == material, "Reloading settings restores the selected material");
                    await Task.Delay(150, timeout.Token);
                    await BackdropUiAsync(hwnd, "screenshot", "--output", Program.ReportPath + $".{theme}.{material.Replace(' ', '-')}.png");
                    if (material is "Mica Alt" or "Acrylic Thin")
                        await EditorUiAsync(localEditor, "screenshot", "--output", Program.ReportPath + $".editor.{theme}.{material.Replace(' ', '-')}.png");
                }
            }
            materialBox.SelectedIndex = 3;
            await WaitFor(() => editors.All(editor => Matches(editor, "Acrylic Thin")));
            EditorElement<CheckBox>(localEditor, "WrapBox").IsChecked = true;
            EditorElement<CheckBox>(localEditor, "LineNumbersBox").IsChecked = false;
            EditorElement<CheckBox>(localEditor, "WhitespaceBox").IsChecked = true;
            EditorElement<ComboBox>(localEditor, "IndentWidthBox").SelectedIndex = 2;
            EditorElement<CheckBox>(localEditor, "UseTabsBox").IsChecked = true;
            var remembered = new TextEditorPreferences
            {
                ReadOnly = false, WordWrap = true, ShowLineNumbers = false,
                ShowWhitespace = true, IndentWidth = 8, UseTabs = true
            };
            await WaitFor(() => shell.Settings.TextEditor == remembered);
            var reloaded = await new SettingsStore(folder).LoadAsync();
            Check(reloaded.TextEditor == remembered, "Editor settings changes persist all six options through the production coordinator");
            var closingControllers = editors.Select(editor => Field<DesktopAcrylicController>((ThinAcrylicBackdrop)editor.SystemBackdrop, "_controller")).ToArray();
            foreach (var editor in editors) DiscardEditor(editor);
            await Task.WhenAll(editors.Select(editor => editor.Completion));
            Check(closingControllers.All(controller => controller.IsClosed), "Closing editors releases their Acrylic Thin controllers");
            materialBox.SelectedIndex = 0;
            await WaitFor(() => shell.Settings.BackdropMaterial == "Mica" && Matches(window, "Mica"));
            Check(true, "Changing settings after editor close does not call closed windows");
            Check(await File.ReadAllTextAsync(localPath) == "original local text" && System.Text.Encoding.UTF8.GetString(remoteService.Content) == "original",
                "Backdrop verification leaves the local and remote fixture content unchanged");
            // Reapply a fresh disk load, matching startup, and open both entry types.
            typeof(MainWindow).GetMethod("ApplySettings", flags)!.Invoke(window, [reloaded]);
            foreach (var remote in new[] { false, true })
            {
                var reopened = new TextFileEditorWindow(remote ? remoteService : new LocalTextFileService(),
                    remote ? "/editor-remote.txt" : localPath, "remembered.txt", remote, root.XamlRoot, hwnd);
                reopened.Activate();
                await WaitFor(() => !EditorElement<ProgressRing>(reopened, "LoadingRing").IsActive);
                var code = EditorElement<CodeEditorControl>(reopened, "CodeEditor").Editor;
                Check(!code.ReadOnly && code.WrapMode == Wrap.Word && code.GetMarginWidthN(0) == 0 &&
                    code.ViewWS == WhiteSpace.VisibleAlways && code.Indent == 8 && code.TabWidth == 8 && code.UseTabs,
                    $"{(remote ? "Remote" : "Local")} editor restores remembered options after disk reload and reopening");
                Check(reopened.TryClose(), "Reopened clean editor closes without creating a draft");
                await reopened.Completion;
            }
            window.Close();
        }
        finally { Directory.Delete(folder, recursive: true); }
        Program.Finish();
    }
}
