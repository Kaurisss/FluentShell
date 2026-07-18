using NovaShell.Models;
using Windows.Security.Credentials;

namespace NovaShell.Services;

public sealed class CredentialService
{
    private const string ResourcePrefix = "NovaShell";
    private const string LegacyResourcePrefix = "SSHUI";
    private readonly PasswordVault _vault = new();

    public CredentialService() => MigrateLegacyCredentials();

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

    public void Remove(ServerProfile profile)
    {
        try
        {
            var existing = _vault.Retrieve(ResourceFor(profile), profile.Username);
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
                .Where(credential => HasPrefix(credential.Resource, ResourcePrefix) || HasPrefix(credential.Resource, LegacyResourcePrefix))
                .ToList();
        }
        catch { return; }
        foreach (var credential in credentials) _vault.Remove(credential);
    }

    private void MigrateLegacyCredentials()
    {
        IReadOnlyList<PasswordCredential> legacyCredentials;
        try
        {
            legacyCredentials = _vault.RetrieveAll()
                .Where(credential => HasPrefix(credential.Resource, LegacyResourcePrefix))
                .ToList();
        }
        catch { return; }

        foreach (var legacyCredential in legacyCredentials)
        {
            try
            {
                legacyCredential.RetrievePassword();
                var resource = ResourcePrefix + legacyCredential.Resource[LegacyResourcePrefix.Length..];

                try
                {
                    _vault.Retrieve(resource, legacyCredential.UserName);
                }
                catch
                {
                    _vault.Add(new PasswordCredential(resource, legacyCredential.UserName, legacyCredential.Password));
                }

                _vault.Remove(legacyCredential);
            }
            catch
            {
                // Keep the legacy entry if migration cannot be completed safely.
            }
        }
    }

    private static bool HasPrefix(string resource, string prefix) =>
        resource.StartsWith(prefix + "/", StringComparison.Ordinal);
}
