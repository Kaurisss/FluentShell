namespace FluentShell.Services;

public static class FileSizeFormatter
{
    public static string Format(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{length / 1024d / 1024d:0.0} MB",
        _ => $"{length / 1024d / 1024d / 1024d:0.0} GB"
    };
}