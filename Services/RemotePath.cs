namespace FluentShell.Services;

public static class RemotePath
{
    public static string Normalize(string currentPath, string path)
    {
        path = path.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(path)) return currentPath;
        if (!path.StartsWith('/')) path = Combine(currentPath, path);

        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    public static string Parent(string path)
    {
        var normalized = Normalize("/", path);
        if (normalized == "/") return "/";

        var slash = normalized.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    public static string Combine(string path, string name) =>
        path == "/"
            ? "/" + name.TrimStart('/')
            : path.TrimEnd('/') + "/" + name.TrimStart('/');
}