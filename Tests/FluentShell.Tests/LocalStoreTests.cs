using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

/// <summary>
/// 生产适配器针对临时目录的往返测试。不涉及凭据保管库。
/// </summary>
[TestClass]
public sealed class LocalStoreTests
{
    private string _folder = string.Empty;

    [TestInitialize]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"fluent-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void RemoveFolder()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [TestMethod]
    public async Task Saved_profiles_survive_a_reload()
    {
        var store = new LocalStore(_folder);
        await store.LoadAsync();

        await store.AddOrUpdateProfileAsync(new ServerProfile
        {
            Name = "日志服务器",
            Host = "logs.example",
            Port = 2222,
            Username = "logic"
        });

        var reloaded = new LocalStore(_folder);
        await reloaded.LoadAsync();
        Assert.HasCount(1, reloaded.Profiles);
        Assert.AreEqual("日志服务器", reloaded.Profiles[0].Name);
        Assert.AreEqual(2222, reloaded.Profiles[0].Port);
    }

    [TestMethod]
    public async Task Copying_a_profile_appends_a_suffixed_duplicate()
    {
        var store = new LocalStore(_folder);
        await store.LoadAsync();
        var source = new ServerProfile { Name = "日志服务器", Host = "logs.example", Username = "logic" };
        await store.AddOrUpdateProfileAsync(source);

        await store.CopyProfileAsync(source);

        var reloaded = new LocalStore(_folder);
        await reloaded.LoadAsync();
        CollectionAssert.AreEqual(
            new[] { "日志服务器", "日志服务器 副本" },
            reloaded.Profiles.Select(profile => profile.Name).ToArray());
        Assert.AreNotEqual(source.Id, reloaded.Profiles[1].Id, "副本必须拥有独立标识。");
    }

    [TestMethod]
    public async Task Deleting_a_profile_persists_the_removal()
    {
        var store = new LocalStore(_folder);
        await store.LoadAsync();
        var profile = new ServerProfile { Name = "日志服务器", Host = "logs.example", Username = "logic" };
        await store.AddOrUpdateProfileAsync(profile);

        await store.DeleteProfileAsync(profile);

        var reloaded = new LocalStore(_folder);
        await reloaded.LoadAsync();
        Assert.IsEmpty(reloaded.Profiles);
    }

    [TestMethod]
    public async Task Settings_survive_a_reload()
    {
        var store = new LocalStore(_folder);
        var settings = await store.LoadAsync();
        settings.TerminalFontSize = 18;
        settings.DownloadDirectory = _folder;
        settings.HasCustomDownloadDirectory = true;

        await store.SaveSettingsAsync(settings);

        var reloaded = await new LocalStore(_folder).LoadAsync();
        Assert.AreEqual(18, reloaded.TerminalFontSize);
        Assert.AreEqual(_folder, reloaded.DownloadDirectory);
    }

    [TestMethod]
    public async Task Loading_an_empty_folder_yields_no_profiles_and_default_settings()
    {
        var store = new LocalStore(_folder);

        var settings = await store.LoadAsync();

        Assert.IsEmpty(store.Profiles);
        Assert.AreEqual(new AppSettings().TerminalFontSize, settings.TerminalFontSize);
    }

    [TestMethod]
    public async Task Clearing_removes_both_documents_and_the_in_memory_profiles()
    {
        var store = new LocalStore(_folder);
        await store.LoadAsync();
        await store.AddOrUpdateProfileAsync(
            new ServerProfile { Name = "日志服务器", Host = "logs.example", Username = "logic" });
        await store.SaveSettingsAsync(new AppSettings { TerminalFontSize = 20 });

        store.ClearAll();

        Assert.IsEmpty(store.Profiles);
        Assert.IsFalse(Directory.Exists(_folder), "清空本机数据会删除整个数据目录。");
    }
}
