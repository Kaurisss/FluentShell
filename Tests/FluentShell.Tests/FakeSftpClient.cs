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
    public List<(string Source, string Destination)> Renames { get; } = [];
    public string? LastListedPath { get; private set; }
    public bool ExistsAnswer { get; set; }

    public IReadOnlyList<RemoteDirectoryEntry> ListDirectory(string path)
    {
        LastListedPath = path;
        return Entries.ToList();
    }

    public void CreateDirectory(string path) => CreatedDirectories.Add(path);

    public bool Exists(string path) => ExistsAnswer;

    public void DeleteDirectory(string path) => DeletedDirectories.Add(path);

    public void DeleteFile(string path) => DeletedFiles.Add(path);

    public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Renames.Add((sourcePath, destinationPath));
        return Task.CompletedTask;
    }

    public void AddDirectory(string name, string fullPath) =>
        Entries.Add(new RemoteDirectoryEntry(name, fullPath, true, 0, default));

    public void AddFile(string name, string fullPath, long length = 0, DateTime lastWriteTime = default) =>
        Entries.Add(new RemoteDirectoryEntry(name, fullPath, false, length, lastWriteTime));
}
