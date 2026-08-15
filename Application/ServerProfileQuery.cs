using FluentShell.Models;

namespace FluentShell.Core;

public enum ServerSortOrder
{
    Name,
    RecentConnection
}

public static class ServerProfileQuery
{
    public static IReadOnlyList<ServerProfile> Apply(
        IEnumerable<ServerProfile> profiles,
        string? filter,
        ServerSortOrder sortOrder)
    {
        var normalizedFilter = filter?.Trim();
        var result = profiles;

        if (!string.IsNullOrWhiteSpace(normalizedFilter))
        {
            result = result.Where(profile =>
                $"{profile.Name} {profile.Host} {profile.Username}"
                    .Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase));
        }

        return (sortOrder == ServerSortOrder.RecentConnection
                ? result.OrderByDescending(profile => profile.LastConnectedAt)
                    .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                : result.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }
}