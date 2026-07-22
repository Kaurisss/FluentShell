namespace FluentShell.Services;

public static class SftpDirectoryEntryPolicy
{
    public static bool ShouldDisplay(string name) => name is not "." and not "..";
}