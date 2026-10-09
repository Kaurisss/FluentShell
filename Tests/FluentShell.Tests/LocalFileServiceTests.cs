using System.Diagnostics;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class LocalFileServiceTests
{
    private string _root = null!;
    private readonly LocalFileService _service = new();

    [TestInitialize]
    public void CreateFixture() => _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "FluentShell-local-actions-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void RemoveFixture() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task Creates_and_renames_local_folders_without_changing_their_contents()
    {
        await _service.CreateDirectoryAsync(_root, "资料");
        File.WriteAllText(Path.Combine(_root, "资料", "内容.txt"), "contents");
        await _service.RenameAsync(_root, "资料", "新资料");
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "资料")));
        Assert.AreEqual("contents", File.ReadAllText(Path.Combine(_root, "新资料", "内容.txt")));
    }

    [TestMethod]
    public async Task File_rename_does_not_overwrite_an_existing_file()
    {
        File.WriteAllText(Path.Combine(_root, "甲.txt"), "first");
        File.WriteAllText(Path.Combine(_root, "乙.txt"), "second");
        await Assert.ThrowsAsync<IOException>(() => _service.RenameAsync(_root, "甲.txt", "乙.txt"));
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(_root, "甲.txt")));
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(_root, "乙.txt")));
        await _service.RenameAsync(_root, "甲.txt", "改名.txt");
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(_root, "改名.txt")));
    }

    [TestMethod]
    public async Task Existing_directory_is_not_reused_as_a_new_folder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "已存在"));
        await Assert.ThrowsAsync<IOException>(() => _service.CreateDirectoryAsync(_root, "已存在"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("../越界")]
    [DataRow("子目录/文件")]
    [DataRow("子目录\\文件")]
    [DataRow("C:\\越界")]
    [DataRow("CON.txt")]
    [DataRow("文件.")]
    [DataRow("文件 ")]
    [DataRow("文件:stream")]
    public async Task Invalid_names_cannot_create_rename_or_recycle_outside_current_directory(string name)
    {
        File.WriteAllText(Path.Combine(_root, "原文件"), "safe");
        var recycled = new List<string>();
        var service = new LocalFileService((path, _) => recycled.Add(path));
        await Assert.ThrowsAsync<IOException>(() => service.CreateDirectoryAsync(_root, name));
        await Assert.ThrowsAsync<IOException>(() => service.RenameAsync(_root, "原文件", name));
        await Assert.ThrowsAsync<IOException>(() => service.RecycleAsync(_root, ["原文件", name]));
        Assert.HasCount(0, recycled);
        Assert.AreEqual("safe", File.ReadAllText(Path.Combine(_root, "原文件")));
    }

    [TestMethod]
    public async Task Mixed_delete_dispatches_each_distinct_selected_child_to_recycling()
    {
        File.WriteAllText(Path.Combine(_root, "文件.txt"), "contents");
        Directory.CreateDirectory(Path.Combine(_root, "文件夹"));
        var recycled = new List<(string Path, bool Directory)>();
        var service = new LocalFileService((path, directory) => recycled.Add((path, directory)));
        await service.RecycleAsync(_root, ["文件.txt", "文件夹", "文件.txt"]);
        CollectionAssert.AreEqual(new[] { (Path.Combine(_root, "文件.txt"), false), (Path.Combine(_root, "文件夹"), true) }, recycled);
    }

    [TestMethod]
    public async Task Folder_size_counts_nested_and_hidden_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
        File.WriteAllBytes(Path.Combine(_root, ".hidden", "child"), [1, 2, 3]);
        using (var file = File.Create(Path.Combine(_root, "large"))) file.SetLength(1024 * 1024);
        Assert.AreEqual(1024L * 1024 + 3, await _service.GetDirectorySizeAsync(_root, CancellationToken.None));
    }

    [TestMethod]
    public async Task Folder_size_can_be_cancelled_and_reports_missing_directories()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.GetDirectorySizeAsync(_root, cancellation.Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.GetDirectorySizeAsync(Path.Combine(_root, "missing"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Junctions_are_not_followed_or_sent_to_recursive_deletion()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "link");
        File.WriteAllBytes(Path.Combine(target, "file"), [1, 2, 3]);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        try
        {
            Assert.IsNull(await _service.GetDirectorySizeAsync(link, CancellationToken.None));
            Assert.AreEqual(3L, await _service.GetDirectorySizeAsync(_root, CancellationToken.None));
            var recycled = false;
            var service = new LocalFileService((_, _) => recycled = true);
            await Assert.ThrowsAsync<IOException>(() => service.RecycleAsync(_root, ["target", "link"]));
            Assert.IsFalse(recycled);
            Assert.AreEqual(3L, new FileInfo(Path.Combine(target, "file")).Length);
        }
        finally { Directory.Delete(link); }
    }
}
