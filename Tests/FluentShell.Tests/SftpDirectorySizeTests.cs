using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpDirectorySizeTests
{
    [TestMethod]
    public async Task Counts_nested_and_hidden_files_with_64_bit_totals_and_ignores_directory_metadata()
    {
        var client = CreateClient();
        client.AddDirectory(".", "/data");
        client.AddDirectory("..", "/");
        client.Entries.Add(new("nested", "/data/nested", true, 4096, default));
        client.AddDirectory(".hidden", "/data/.hidden");
        client.AddFile("large", "/data/large", 5L * 1024 * 1024 * 1024);
        client.AddFile(".secret", "/data/.secret", 13);
        client.AddFile("child", "/data/nested/child", 42);
        client.AddFile("hidden-child", "/data/.hidden/hidden-child", 27);
        client.AddFile("outside", "/outside", 999);
        var service = new SftpFileService(() => client);

        Assert.AreEqual(5L * 1024 * 1024 * 1024 + 82,
            await service.GetDirectorySizeAsync("/data", CancellationToken.None));
    }

    [TestMethod]
    public async Task Empty_directory_has_zero_size()
    {
        var client = CreateClient();
        var service = new SftpFileService(() => client);
        Assert.AreEqual(0L, await service.GetDirectorySizeAsync("/data", CancellationToken.None));
    }

    [TestMethod]
    public async Task Does_not_follow_directory_or_file_links_including_cycles()
    {
        var client = CreateClient();
        client.Entries.Add(new("cycle", "/data/cycle", true, 1000, default, true));
        client.Entries.Add(new("file-link", "/data/file-link", false, 2000, default, true));
        client.AddFile("real", "/data/real", 17);
        var service = new SftpFileService(() => client);
        Assert.AreEqual(17L, await service.GetDirectorySizeAsync("/data", CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "/", "/data" }, client.ListedPaths);
    }

    [TestMethod]
    public async Task Refuses_to_follow_selected_directory_that_became_a_link()
    {
        var client = new FakeSftpClient();
        client.Entries.Add(new("data", "/data", true, 0, default, true));
        var service = new SftpFileService(() => client);
        await Assert.ThrowsAsync<IOException>(() => service.GetDirectorySizeAsync("/data", CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "/" }, client.ListedPaths);
    }

    [TestMethod]
    public async Task Child_paths_are_built_from_names_instead_of_untrusted_full_paths()
    {
        var client = CreateClient();
        client.ListingsByPath["/data"] = [new("nested", "/elsewhere", true, 0, default)];
        client.ListingsByPath["/data/nested"] = [new("file", "/elsewhere/file", false, 41, default)];
        var service = new SftpFileService(() => client);
        Assert.AreEqual(41L, await service.GetDirectorySizeAsync("/data", CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "/", "/data", "/data/nested" }, client.ListedPaths);
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("nested/file")]
    [DataRow("/escape")]
    public async Task Invalid_child_names_fail_instead_of_producing_an_incomplete_total(string name)
    {
        var client = CreateClient();
        client.ListingsByPath["/data"] = [new(name, "/escape", true, 0, default)];
        var service = new SftpFileService(() => client);
        await Assert.ThrowsAsync<IOException>(() => service.GetDirectorySizeAsync("/data", CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "/", "/data" }, client.ListedPaths);
    }

    [TestMethod]
    public async Task Permission_error_does_not_return_partial_size_and_releases_client_gate()
    {
        var client = CreateClient();
        client.AddFile("readable", "/data/readable", 123);
        client.AddDirectory("denied", "/data/denied");
        client.ListExceptions["/data/denied"] = new IOException("Permission denied");
        var service = new SftpFileService(() => client);

        var error = await Assert.ThrowsAsync<IOException>(() => service.GetDirectorySizeAsync("/data", CancellationToken.None));
        Assert.AreEqual("Permission denied", error.Message);
        Assert.HasCount(1, await service.ListDirectoryAsync("/"));
    }

    [TestMethod]
    public async Task Pre_cancelled_query_does_not_contact_server()
    {
        var client = CreateClient();
        var service = new SftpFileService(() => client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetDirectorySizeAsync("/data", cancellation.Token));
        Assert.IsEmpty(client.ListedPaths);
    }

    [TestMethod]
    public async Task Cancelling_an_in_flight_read_stops_recursion_and_queues_browsing_until_client_is_available()
    {
        var client = CreateClient();
        client.AddDirectory("nested", "/data/nested");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        client.OnList = path =>
        {
            if (path != "/data") return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test read was not released.");
        };
        var service = new SftpFileService(() => client);
        using var cancellation = new CancellationTokenSource();
        var calculation = service.GetDirectorySizeAsync("/data", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var browse = service.ListDirectoryAsync("/other");
            Assert.IsFalse(browse.IsCompleted);
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAsync<OperationCanceledException>(() => calculation.WaitAsync(TimeSpan.FromSeconds(5)));
            await browse.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "/", "/data", "/other" }, client.ListedPaths);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Unknown_file_length_is_an_error_instead_of_a_negative_total()
    {
        var client = CreateClient();
        client.AddFile("unknown", "/data/unknown", -1);
        var service = new SftpFileService(() => client);
        await Assert.ThrowsAsync<IOException>(() => service.GetDirectorySizeAsync("/data", CancellationToken.None));
    }

    [TestMethod]
    public async Task Overflow_is_an_error_instead_of_a_wrapped_total()
    {
        var client = CreateClient();
        client.AddFile("first", "/data/first", long.MaxValue);
        client.AddFile("second", "/data/second", 1);
        var service = new SftpFileService(() => client);
        await Assert.ThrowsAsync<OverflowException>(() => service.GetDirectorySizeAsync("/data", CancellationToken.None));
    }

    private static FakeSftpClient CreateClient()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("data", "/data");
        return client;
    }
}
