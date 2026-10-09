using FluentShell.Models;
using Microsoft.UI.Xaml;

namespace FluentShell.Views.Shell;

// The owner root supplies defaults and the coordinator's serialized save path.
// Editors keep their own current options; remembered defaults apply on opening.
public abstract class TextEditorSettings : DependencyObject
{
    private sealed record Context(TextEditorPreferences Preferences, Func<TextEditorPreferences, Task> Save);
    private static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached(
        "Context", typeof(object), typeof(TextEditorSettings), new PropertyMetadata(null));

    public static void Configure(DependencyObject root, TextEditorPreferences preferences, Func<TextEditorPreferences, Task> save) =>
        root.SetValue(ContextProperty, new Context(preferences.Normalize(), save));

    public static TextEditorPreferences GetPreferences(DependencyObject? root) =>
        (root?.GetValue(ContextProperty) as Context)?.Preferences ?? new();

    public static Task SaveAsync(DependencyObject? root, TextEditorPreferences preferences) =>
        (root?.GetValue(ContextProperty) as Context)?.Save(preferences.Normalize()) ?? Task.CompletedTask;
}
