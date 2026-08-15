using System.Security.Cryptography;
using FluentShell.Services;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace FluentShell.Tests;

[TestClass]
public sealed class PrivateKeyValidatorTests
{
    private readonly PrivateKeyValidator _validator = new();

    [TestMethod]
    public async Task ValidateAsync_accepts_an_unencrypted_rsa_private_key()
    {
        var path = CreateUnencryptedPrivateKey();
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsTrue(result.IsValid);
            Assert.IsFalse(result.RequiresPassphrase);
            Assert.IsNull(result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_accepts_an_unencrypted_ecdsa_private_key()
    {
        var path = CreateUnencryptedEcdsaPrivateKey();
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsTrue(result.IsValid);
            Assert.IsFalse(result.RequiresPassphrase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_accepts_an_unencrypted_ed25519_private_key()
    {
        var path = CreateUnencryptedEd25519PrivateKey();
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsTrue(result.IsValid);
            Assert.IsFalse(result.RequiresPassphrase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_marks_an_encrypted_private_key_as_requiring_a_passphrase()
    {
        var path = CreateEncryptedPrivateKey("test-passphrase");
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsTrue(result.IsValid);
            Assert.IsTrue(result.RequiresPassphrase);
            Assert.AreEqual(PrivateKeyValidator.PassphraseRequiredMessage, result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_rejects_a_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fluent-shell-{Guid.NewGuid():N}.pem");

        var result = await _validator.ValidateAsync(path);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(PrivateKeyValidator.FileNotFoundMessage, result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAsync_rejects_null_and_blank_paths()
    {
        var nullResult = await _validator.ValidateAsync(null);
        var emptyResult = await _validator.ValidateAsync(string.Empty);
        var whitespaceResult = await _validator.ValidateAsync("   ");

        Assert.IsFalse(nullResult.IsValid);
        Assert.IsFalse(emptyResult.IsValid);
        Assert.IsFalse(whitespaceResult.IsValid);
        Assert.AreEqual(PrivateKeyValidator.FileNotFoundMessage, nullResult.ErrorMessage);
        Assert.AreEqual(PrivateKeyValidator.FileNotFoundMessage, emptyResult.ErrorMessage);
        Assert.AreEqual(PrivateKeyValidator.FileNotFoundMessage, whitespaceResult.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateAsync_accepts_a_private_key_at_a_unicode_path()
    {
        using var key = RSA.Create(2048);
        var path = CreateTemporaryFile(key.ExportRSAPrivateKeyPem(), "私钥");
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsTrue(result.IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_rejects_an_invalid_key_format()
    {
        var path = CreateTemporaryFile("这不是私钥文件。");
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(PrivateKeyValidator.InvalidFormatMessage, result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_reports_a_truncated_private_key_as_corrupted()
    {
        var path = CreateTemporaryFile("-----BEGIN PRIVATE KEY-----\ntruncated");
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(PrivateKeyValidator.CorruptedKeyMessage, result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_rejects_a_putty_private_key_before_connection()
    {
        var path = CreateTemporaryFile("PuTTY-User-Key-File-3: ssh-ed25519");
        try
        {
            var result = await _validator.ValidateAsync(path);

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(PrivateKeyValidator.InvalidFormatMessage, result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_reports_a_file_that_cannot_be_opened()
    {
        var path = CreateTemporaryFile("private key");
        try
        {
            using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = await _validator.ValidateAsync(path);

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(PrivateKeyValidator.FileNotReadableMessage, result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_honors_a_pre_cancelled_token()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _validator.ValidateAsync("ignored", cancellationSource.Token));
    }

    private static string CreateUnencryptedPrivateKey()
    {
        using var key = RSA.Create(2048);
        return CreateTemporaryFile(key.ExportRSAPrivateKeyPem());
    }

    private static string CreateUnencryptedEcdsaPrivateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return CreateTemporaryFile(key.ExportECPrivateKeyPem());
    }

    private static string CreateUnencryptedEd25519PrivateKey()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var keyPair = generator.GenerateKeyPair();
        var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private);
        using var textWriter = new StringWriter();
        var pemWriter = new PemWriter(textWriter);
        pemWriter.WriteObject(privateKeyInfo);
        return CreateTemporaryFile(textWriter.ToString());
    }

    private static string CreateEncryptedPrivateKey(string passphrase)
    {
        using var key = RSA.Create(2048);
        var encryption = new PbeParameters(
            PbeEncryptionAlgorithm.Aes256Cbc,
            HashAlgorithmName.SHA256,
            100_000);
        return CreateTemporaryFile(key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, encryption));
    }

    private static string CreateTemporaryFile(string content, string fileNamePrefix = "fluent-shell")
    {
        var path = Path.Combine(Path.GetTempPath(), $"{fileNamePrefix}-{Guid.NewGuid():N}.pem");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        return path;
    }
}
