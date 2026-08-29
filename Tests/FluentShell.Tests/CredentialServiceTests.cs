using System.Runtime.InteropServices;
using FluentShell.Models;
using FluentShell.Services;
using Windows.Security.Credentials;

namespace FluentShell.Tests;

[TestClass]
public sealed class CredentialServiceTests
{
    private const string CurrentPrefix = "FluentShell";
    private const string FixtureSecret = "fixture-password";
    private static readonly Guid ProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public void TryGet_reads_a_current_credential()
    {
        var profile = CreateProfile();
        var vault = new FakePasswordVault();
        vault.Add(CreateCredential(CurrentPrefix, profile, FixtureSecret));

        var service = new CredentialService(vault);

        Assert.AreEqual(FixtureSecret, service.TryGet(profile));
    }

    [TestMethod]
    public void TryGet_migrates_a_NovaShell_credential_to_the_current_resource()
    {
        var profile = CreateProfile();
        var vault = new FakePasswordVault();
        vault.Add(CreateCredential("NovaShell", profile, FixtureSecret));

        var service = new CredentialService(vault);

        Assert.AreEqual(FixtureSecret, service.TryGet(profile));
        Assert.IsTrue(vault.Contains(CurrentPrefix, profile));
        Assert.IsFalse(vault.Contains("NovaShell", profile));
    }

    [TestMethod]
    public void Save_replaces_current_and_legacy_credentials()
    {
        var profile = CreateProfile();
        var vault = new FakePasswordVault();
        vault.Add(CreateCredential(CurrentPrefix, profile, "old-current"));
        vault.Add(CreateCredential("SSHUI", profile, "old-legacy"));

        var service = new CredentialService(vault);
        service.Save(profile, FixtureSecret);

        Assert.AreEqual(FixtureSecret, service.TryGet(profile));
        Assert.AreEqual(1, vault.Count(CurrentPrefix, profile));
        Assert.IsFalse(vault.Contains("SSHUI", profile));
    }

    [TestMethod]
    public void ClearAll_removes_credentials_from_all_product_generations()
    {
        var profile = CreateProfile();
        var vault = new FakePasswordVault();
        vault.Add(CreateCredential(CurrentPrefix, profile, FixtureSecret));
        vault.Add(CreateCredential("NovaShell", profile, FixtureSecret));
        vault.Add(CreateCredential("SSHUI", profile, FixtureSecret));
        vault.Add(new PasswordCredential("OtherApp/profile", profile.Username, FixtureSecret));

        new CredentialService(vault).ClearAll();

        Assert.HasCount(1, vault.All);
        Assert.AreEqual("OtherApp/profile", vault.All[0].Resource);
    }

    [TestMethod]
    public void TryGet_returns_null_when_the_vault_reports_no_credentials()
    {
        var vault = new FakePasswordVault
        {
            RetrieveAllException = new COMException("missing", unchecked((int)0x80070490))
        };

        var result = new CredentialService(vault).TryGet(CreateProfile());

        Assert.IsNull(result);
    }

    private static ServerProfile CreateProfile() => new()
    {
        Id = ProfileId,
        Username = "root"
    };

    private static PasswordCredential CreateCredential(
        string prefix,
        ServerProfile profile,
        string secret) =>
        new($"{prefix}/{profile.Id:N}", profile.Username, secret);

    private sealed class FakePasswordVault : IPasswordVault
    {
        public List<PasswordCredential> All { get; } = [];

        public Exception? RetrieveAllException { get; init; }

        public IReadOnlyList<PasswordCredential> RetrieveAll()
        {
            if (RetrieveAllException is not null) throw RetrieveAllException;
            return [.. All];
        }

        public void Add(PasswordCredential credential) => All.Add(credential);

        public void Remove(PasswordCredential credential) => All.Remove(credential);

        public bool Contains(string prefix, ServerProfile profile) =>
            All.Any(credential =>
                credential.Resource == $"{prefix}/{profile.Id:N}" &&
                credential.UserName == profile.Username);

        public int Count(string prefix, ServerProfile profile) =>
            All.Count(credential =>
                credential.Resource == $"{prefix}/{profile.Id:N}" &&
                credential.UserName == profile.Username);
    }
}
