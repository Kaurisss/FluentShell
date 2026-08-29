using System.Runtime.InteropServices;
using FluentShell.Models;
using Windows.Security.Credentials;

namespace FluentShell.Services;

public sealed class CredentialService
{
    private const string ResourcePrefix = "FluentShell";
    private static readonly string[] ResourcePrefixes = [ResourcePrefix, "NovaShell", "SSHUI"];
    private readonly IPasswordVault _vault;

    public CredentialService() : this(new WindowsPasswordVault()) { }

    internal CredentialService(IPasswordVault vault) => _vault = vault;

    public string? TryGet(ServerProfile profile)
    {
        var credentials = TryRetrieveAll();
        foreach (var prefix in ResourcePrefixes)
        {
            var resource = ResourceFor(prefix, profile.Id);
            var credential = credentials.FirstOrDefault(candidate =>
                Matches(candidate, resource, profile.Username));
            if (credential is null) continue;

            var secret = TryReadPassword(credential);
            if (secret is null) continue;

            if (!string.Equals(prefix, ResourcePrefix, StringComparison.Ordinal))
                TryMigrate(credential, profile, secret, credentials);
            return secret;
        }
        return null;
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
        var credentials = TryRetrieveAll()
            .Where(credential => ResourcePrefixes.Any(prefix =>
                Matches(credential, ResourceFor(prefix, profileId), username)))
            .ToList();

        foreach (var credential in credentials) TryRemove(credential);
    }

    public void ClearAll()
    {
        var credentials = TryRetrieveAll()
            .Where(credential => ResourcePrefixes.Any(prefix =>
                HasPrefix(credential.Resource, prefix)))
            .ToList();

        foreach (var credential in credentials) TryRemove(credential);
    }

    private IReadOnlyList<PasswordCredential> TryRetrieveAll()
    {
        try
        {
            return _vault.RetrieveAll();
        }
        catch (COMException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string? TryReadPassword(PasswordCredential credential)
    {
        try
        {
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryMigrate(
        PasswordCredential legacyCredential,
        ServerProfile profile,
        string secret,
        IReadOnlyList<PasswordCredential> snapshot)
    {
        try
        {
            var currentResource = ResourceFor(profile);
            var hasCurrentCredential = snapshot.Any(candidate =>
                Matches(candidate, currentResource, profile.Username));
            if (!hasCurrentCredential)
            {
                _vault.Add(new PasswordCredential(currentResource, profile.Username, secret));
            }

            // Remove the old entry only after the current entry exists.
            _vault.Remove(legacyCredential);
        }
        catch (COMException)
        {
            // Keep the legacy entry when the vault cannot complete migration.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the legacy entry when the vault cannot complete migration.
        }
    }

    private void TryRemove(PasswordCredential credential)
    {
        try
        {
            _vault.Remove(credential);
        }
        catch (COMException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResourceFor(ServerProfile profile) => ResourceFor(ResourcePrefix, profile.Id);

    private static string ResourceFor(string prefix, Guid profileId) => $"{prefix}/{profileId:N}";

    private static bool Matches(PasswordCredential credential, string resource, string username) =>
        string.Equals(credential.Resource, resource, StringComparison.Ordinal) &&
        string.Equals(credential.UserName, username, StringComparison.Ordinal);

    private static bool HasPrefix(string resource, string prefix) =>
        resource.StartsWith(prefix + "/", StringComparison.Ordinal);
}
