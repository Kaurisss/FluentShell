namespace FluentShell.Services;

internal static class AppDataPaths
{
    public const string CurrentProductName = "FluentShell";
    private static readonly string[] LegacyProductNames = ["NovaShell", "SSHUI"];

    public static string Folder { get; } = InitializeFolder();

    private static string InitializeFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var currentFolder = Path.Combine(localAppData, CurrentProductName);
        var legacyFolders = LegacyProductNames
            .Select(name => Path.Combine(localAppData, name))
            .Where(Directory.Exists)
            .ToArray();

        if (!Directory.Exists(currentFolder))
        {
            foreach (var legacyFolder in legacyFolders)
            {
                try
                {
                    Directory.Move(legacyFolder, currentFolder);
                    return currentFolder;
                }
                catch (IOException)
                {
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
            }
        }

        Directory.CreateDirectory(currentFolder);
        foreach (var legacyFolder in legacyFolders)
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

        try
        {
            File.Copy(source, destination);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
