using FluentShell.Core;
using FluentShell.Models;

namespace FluentShell.Tests;

[TestClass]
public sealed class OverviewConnectionQueryTests
{
    [TestMethod]
    public void Apply_returns_empty_when_no_profiles_are_saved()
    {
        var result = OverviewConnectionQuery.Apply([]);

        Assert.AreEqual(OverviewConnectionMode.Empty, result.Mode);
        Assert.IsEmpty(result.Profiles);
    }

    [TestMethod]
    public void Apply_returns_named_saved_profiles_when_none_have_connected()
    {
        var result = OverviewConnectionQuery.Apply(
        [
            Profile("Zulu"),
            Profile("Charlie"),
            Profile("Bravo"),
            Profile("Alpha")
        ]);

        Assert.AreEqual(OverviewConnectionMode.SavedFallback, result.Mode);
        CollectionAssert.AreEqual(
            new[] { "Alpha", "Bravo", "Charlie" },
            result.Profiles.Select(profile => profile.Name).ToArray());
    }

    [TestMethod]
    public void Apply_returns_the_three_most_recent_successful_connections()
    {
        var result = OverviewConnectionQuery.Apply(
        [
            Profile("Old", new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero)),
            Profile("Newest", new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            Profile("Never"),
            Profile("Second", new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero)),
            Profile("Third", new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero))
        ]);

        Assert.AreEqual(OverviewConnectionMode.Recent, result.Mode);
        CollectionAssert.AreEqual(
            new[] { "Newest", "Second", "Third" },
            result.Profiles.Select(profile => profile.Name).ToArray());
    }

    [TestMethod]
    public void Apply_orders_connections_with_the_same_time_by_name()
    {
        var connectedAt = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);

        var result = OverviewConnectionQuery.Apply([Profile("Zulu", connectedAt), Profile("Alpha", connectedAt)]);

        Assert.AreEqual(OverviewConnectionMode.Recent, result.Mode);
        CollectionAssert.AreEqual(
            new[] { "Alpha", "Zulu" },
            result.Profiles.Select(profile => profile.Name).ToArray());
    }

    private static ServerProfile Profile(string name, DateTimeOffset? lastConnectedAt = null) => new()
    {
        Name = name,
        Host = $"{name.ToLowerInvariant()}.example.com",
        Username = "deploy",
        LastConnectedAt = lastConnectedAt
    };
}