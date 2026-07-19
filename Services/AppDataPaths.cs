namespace FluentShell.Services;

internal static class AppDataPaths
{
    public const string CurrentProductName = "FluentShell";
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        CurrentProductName);
}
