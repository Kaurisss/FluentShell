using FluentShell.Models;
using Windows.Security.Credentials;

namespace FluentShell.Services;

public sealed class CredentialService
{
    private const string ResourcePrefix = "FluentShell";
    private readonly PasswordVault _vault = new();

    private static string ResourceFor(ServerProfile profile) => $"{ResourcePrefix}/{profile.Id:N}";

    public string? TryGet(ServerProfile profile)
    {
        try
        {
            var credential = _vault.Retrieve(ResourceFor(profile), profile.Username);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch { return null; }
    }

    public void Save(ServerProfile profile, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        Remove(profile);
        _vault.Add(new PasswordCredential(ResourceFor(profile), profile.Username, secret));
    }

    public void Remove(ServerProfile profile) => Remove(profile.Id, profile.Username);

    public void Remove(Guid profileId, string username)
    {
        try
        {
            var existing = _vault.Retrieve($"{ResourcePrefix}/{profileId:N}", username);
            _vault.Remove(existing);
        }
        catch { }
    }

    public void ClearAll()
    {
        IReadOnlyList<PasswordCredential> credentials;
        try
        {
            credentials = _vault.RetrieveAll()
                .Where(credential => HasPrefix(credential.Resource, ResourcePrefix))
                .ToList();
        }
        catch { return; }
        foreach (var credential in credentials) _vault.Remove(credential);
    }

    private static bool HasPrefix(string resource, string prefix) =>
        resource.StartsWith(prefix + "/", StringComparison.Ordinal);
}
