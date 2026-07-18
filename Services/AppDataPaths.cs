namespace NovaShell.Services;

internal static class AppDataPaths
{
    public const string CurrentProductName = "NovaShell";
    public const string LegacyProductName = "SSHUI";

    public static string Folder { get; } = InitializeFolder();

    private static string InitializeFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var currentFolder = Path.Combine(localAppData, CurrentProductName);
        var legacyFolder = Path.Combine(localAppData, LegacyProductName);

        if (!Directory.Exists(currentFolder) && Directory.Exists(legacyFolder))
        {
            try
            {
                Directory.Move(legacyFolder, currentFolder);
                return currentFolder;
            }
            catch
            {
                // If another process is using the legacy directory, keep it and copy
                // only the known configuration files below.
            }
        }

        Directory.CreateDirectory(currentFolder);
        if (Directory.Exists(legacyFolder))
        {
            CopyIfMissing(legacyFolder, currentFolder, "servers.json");
            CopyIfMissing(legacyFolder, currentFolder, "settings.json");
        }

        return currentFolder;
    }

    private static void CopyIfMissing(string sourceFolder, string destinationFolder, string fileName)
    {
        var source = Path.Combine(sourceFolder, fileName);
        var destination = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(source) || File.Exists(destination)) return;

        try { File.Copy(source, destination); }
        catch { }
    }
}
