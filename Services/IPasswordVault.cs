using Windows.Security.Credentials;

namespace FluentShell.Services;

internal interface IPasswordVault
{
    IReadOnlyList<PasswordCredential> RetrieveAll();
    void Add(PasswordCredential credential);
    void Remove(PasswordCredential credential);
}

internal sealed class WindowsPasswordVault : IPasswordVault
{
    private readonly PasswordVault _vault = new();

    public IReadOnlyList<PasswordCredential> RetrieveAll() => _vault.RetrieveAll();

    public void Add(PasswordCredential credential) => _vault.Add(credential);

    public void Remove(PasswordCredential credential) => _vault.Remove(credential);
}
