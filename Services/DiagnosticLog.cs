using System.Runtime.InteropServices;

namespace FluentShell.Services;

public static class DiagnosticLog
{
    public static string Folder => Path.Combine(AppDataPaths.Folder, "Logs");
    public static string Summary => $"FluentShell {typeof(App).Assembly.GetName().Version}\n{RuntimeInformation.OSDescription}\n进程架构：{RuntimeInformation.ProcessArchitecture}\n.NET {Environment.Version}";

    public static void Record(string eventName)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, "application.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".previous", true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {eventName}\n");
        }
        catch { /* Diagnostics must never prevent application startup. */ }
    }
}
