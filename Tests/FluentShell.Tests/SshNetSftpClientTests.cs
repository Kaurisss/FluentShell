using System.Reflection;
using FluentShell.Services;
using Renci.SshNet.Sftp;

namespace FluentShell.Tests;

[TestClass]
public sealed class SshNetSftpClientTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Deletion_uses_the_listed_entry_without_resolving_its_link_target(bool directory)
    {
        var deleted = false;
        var listed = new List<string>();
        var file = CreateProxy<ISftpFile>((method, _) => method.Name switch
        {
            "get_Name" => "link",
            "Delete" => RecordDelete(),
            _ => throw new AssertFailedException("Unexpected file API: " + method.Name)
        });
        var client = CreateProxy<Renci.SshNet.ISftpClient>((method, args) => method.Name switch
        {
            "ListDirectory" => ListParent((string)args![0]!),
            _ => throw new AssertFailedException("Unsafe or unexpected client API: " + method.Name)
        });
        var adapter = new SshNetSftpClient(client);

        if (directory) adapter.DeleteDirectory("/Mod/link");
        else adapter.DeleteFile("/Mod/link");

        Assert.IsTrue(deleted);
        CollectionAssert.AreEqual(new[] { "/Mod" }, listed);

        object? RecordDelete() { deleted = true; return null; }
        IEnumerable<ISftpFile> ListParent(string path) { listed.Add(path); return [file]; }
    }

    [TestMethod]
    public void Deleting_many_siblings_reuses_one_listing_instead_of_reading_it_for_every_file()
    {
        var listed = 0;
        var deleted = new List<string>();
        var files = Enumerable.Range(0, 1000).Select(i =>
        {
            var name = "file" + i;
            return CreateProxy<ISftpFile>((method, _) => method.Name switch
            {
                "get_Name" => name,
                "Delete" => RecordDelete(name),
                _ => throw new AssertFailedException("Unexpected file API: " + method.Name)
            });
        }).ToArray();
        var client = CreateProxy<Renci.SshNet.ISftpClient>((method, _) => method.Name == "ListDirectory"
            ? ListParent() : throw new AssertFailedException("Unexpected client API: " + method.Name));
        var adapter = new SshNetSftpClient(client);

        for (var i = 0; i < 1000; i++) adapter.DeleteFile("/Mod/file" + i);

        Assert.AreEqual(1, listed);
        Assert.HasCount(1000, deleted);
        object? RecordDelete(string name) { deleted.Add(name); return null; }
        IEnumerable<ISftpFile> ListParent() { listed++; return files; }
    }

    [TestMethod]
    public async Task Recursive_deletion_is_available_only_when_an_ssh_executor_was_supplied()
    {
        var paths = new List<string>();
        var client = CreateProxy<Renci.SshNet.ISftpClient>((method, _) =>
            throw new AssertFailedException("Server-side deletion must not call SFTP: " + method.Name));
        var fileOnly = new SshNetSftpClient(client);
        var ssh = new SshNetSftpClient(client, path => { paths.Add(path); return Task.CompletedTask; });

        Assert.IsFalse(await fileOnly.TryDeleteDirectoryRecursivelyAsync("/Mod"));
        Assert.IsTrue(await ssh.TryDeleteDirectoryRecursivelyAsync("/Mod"));
        CollectionAssert.AreEqual(new[] { "/Mod" }, paths);
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ApiProxy>();
        ((ApiProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class ApiProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
