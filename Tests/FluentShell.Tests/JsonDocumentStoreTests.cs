using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class JsonDocumentStoreTests
{
    private sealed record Document(string Name, int Count);

    private static readonly Document Default = new("默认", 0);

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
    public async Task Missing_file_returns_the_default()
    {
        Assert.AreEqual(Default, await CreateStore().LoadAsync());
    }

    [TestMethod]
    public async Task Saved_document_round_trips()
    {
        await CreateStore().SaveAsync(new Document("日志服务器", 3));

        Assert.AreEqual(new Document("日志服务器", 3), await CreateStore().LoadAsync());
    }

    [TestMethod]
    public async Task Corrupt_file_falls_back_to_the_default_instead_of_throwing()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "doc.json"), "{ 这不是 JSON");

        Assert.AreEqual(Default, await CreateStore().LoadAsync());
    }

    [TestMethod]
    public async Task Save_leaves_no_temporary_file_behind()
    {
        await CreateStore().SaveAsync(new Document("日志服务器", 1));

        CollectionAssert.AreEqual(
            new[] { "doc.json" },
            Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task Save_overwrites_an_existing_document()
    {
        await CreateStore().SaveAsync(new Document("旧值", 1));
        await CreateStore().SaveAsync(new Document("新值", 2));

        Assert.AreEqual(new Document("新值", 2), await CreateStore().LoadAsync());
    }

    [TestMethod]
    public async Task Save_creates_a_missing_folder()
    {
        Directory.Delete(_folder, true);

        await CreateStore().SaveAsync(new Document("日志服务器", 1));

        Assert.IsTrue(File.Exists(Path.Combine(_folder, "doc.json")));
    }

    private JsonDocumentStore<Document> CreateStore() =>
        new(_folder, "doc.json", static () => Default);
}
