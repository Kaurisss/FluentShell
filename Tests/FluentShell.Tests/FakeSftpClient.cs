using FluentShell.Services;

namespace FluentShell.Tests;

/// <summary><see cref="ISftpClient"/> 的测试适配器：目录内容可预置，远程操作只记录调用。</summary>
internal sealed class FakeSftpClient : ISftpClient
{
    public bool IsConnected { get; set; } = true;
    public List<RemoteDirectoryEntry> Entries { get; } = [];
    public List<string> CreatedDirectories { get; } = [];
    public List<string> DeletedDirectories { get; } = [];
    public List<string> DeletedFiles { get; } = [];
    public List<(bool IsDirectory, string Path)> Deletions { get; } = [];
    public List<string> ListedPaths { get; } = [];
    public Dictionary<string, Exception> DeleteExceptions { get; } = [];
    public Dictionary<string, IReadOnlyList<RemoteDirectoryEntry>> ListingsByPath { get; } = [];
    public Func<string, Task<bool>>? RecursiveDeleteHandler { get; set; }
    public List<string> RecursiveDeletes { get; } = [];
    public List<(string Source, string Destination)> Renames { get; } = [];
    public string? LastListedPath { get; private set; }
    public bool ExistsAnswer { get; set; }

    public IReadOnlyList<RemoteDirectoryEntry> ListDirectory(string path)
    {
        LastListedPath = path;
        ListedPaths.Add(path);
        if (ListingsByPath.TryGetValue(path, out var listing)) return listing;
        return Entries.Where(entry => entry.Name is "." or ".." || RemotePath.Parent(entry.FullPath) == path).ToList();
    }

    public void CreateDirectory(string path) => CreatedDirectories.Add(path);

    public bool Exists(string path) => ExistsAnswer;

    public bool IsDirectory(string path) => Entries.Any(entry => entry.FullPath == path && entry.IsDirectory);

    public void DeleteDirectory(string path)
    {
        if (DeleteExceptions.TryGetValue(path, out var exception)) throw exception;
        if (Entries.Any(entry => entry.Name is not "." and not ".." && RemotePath.Parent(entry.FullPath) == path))
            throw new IOException("Directory is not empty.");
        DeletedDirectories.Add(path);
        Deletions.Add((true, path));
        Entries.RemoveAll(entry => entry.FullPath == path);
    }

    public void DeleteFile(string path)
    {
        if (DeleteExceptions.TryGetValue(path, out var exception)) throw exception;
        DeletedFiles.Add(path);
        Deletions.Add((false, path));
        Entries.RemoveAll(entry => entry.FullPath == path);
    }

    public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Renames.Add((sourcePath, destinationPath));
        return Task.CompletedTask;
    }

    public Task<bool> TryDeleteDirectoryRecursivelyAsync(string path)
    {
        RecursiveDeletes.Add(path);
        return RecursiveDeleteHandler?.Invoke(path) ?? Task.FromResult(false);
    }

    public void AddDirectory(string name, string fullPath) =>
        Entries.Add(new RemoteDirectoryEntry(name, fullPath, true, 0, default));

    public void AddFile(string name, string fullPath, long length = 0, DateTime lastWriteTime = default) =>
        Entries.Add(new RemoteDirectoryEntry(name, fullPath, false, length, lastWriteTime));
}
