using FluentShell.Models;

namespace FluentShell.Core;

public enum OverviewConnectionMode
{
    Empty,
    Recent,
    SavedFallback
}

public sealed record OverviewConnectionState(
    OverviewConnectionMode Mode,
    IReadOnlyList<ServerProfile> Profiles);

public static class OverviewConnectionQuery
{
    private const int DisplayLimit = 3;

    public static OverviewConnectionState Apply(IReadOnlyList<ServerProfile> profiles)
    {
        if (profiles.Count == 0)
            return new(OverviewConnectionMode.Empty, []);

        var recent = ServerProfileQuery.Apply(profiles, null, ServerSortOrder.RecentConnection)
            .Where(profile => profile.LastConnectedAt is not null)
            .Take(DisplayLimit)
            .ToList();

        return recent.Count > 0
            ? new(OverviewConnectionMode.Recent, recent)
            : new(
                OverviewConnectionMode.SavedFallback,
                ServerProfileQuery.Apply(profiles, null, ServerSortOrder.Name).Take(DisplayLimit).ToList());
    }
}