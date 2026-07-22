using FluentShell.Core;
using FluentShell.Models;

namespace FluentShell.Tests;

[TestClass]
public sealed class ServerProfileQueryTests
{
    [TestMethod]
    public void Apply_filters_name_host_and_username_case_insensitively()
    {
        var profiles = new[]
        {
            Profile("Production", "prod.example.com", "deploy"),
            Profile("Development", "dev.example.com", "developer")
        };

        var result = ServerProfileQuery.Apply(profiles, "DEPLOY", ServerSortOrder.Name);

        CollectionAssert.AreEqual(new[] { "Production" }, result.Select(profile => profile.Name).ToArray());
    }

    [TestMethod]
    public void Apply_sorts_recent_connections_then_name()
    {
        var newest = Profile("Zulu", "z.example.com", "z", new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var sameTimeByName = Profile("Alpha", "a.example.com", "a", newest.LastConnectedAt);
        var never = Profile("Never", "n.example.com", "n");

        var result = ServerProfileQuery.Apply(
            new[] { newest, never, sameTimeByName },
            null,
            ServerSortOrder.RecentConnection);

        CollectionAssert.AreEqual(
            new[] { "Alpha", "Zulu", "Never" },
            result.Select(profile => profile.Name).ToArray());
    }

    private static ServerProfile Profile(
        string name,
        string host,
        string username,
        DateTimeOffset? lastConnectedAt = null) => new()
    {
        Name = name,
        Host = host,
        Username = username,
        LastConnectedAt = lastConnectedAt
    };
}