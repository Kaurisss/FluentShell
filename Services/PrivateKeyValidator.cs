using System.Security;
using System.Text;
using Org.BouncyCastle.Crypto;
using Renci.SshNet.Common;

namespace FluentShell.Services;

public sealed record PrivateKeyValidationResult(
    bool IsValid,
    string? ErrorMessage,
    bool RequiresPassphrase);

/// <summary>
/// 在连接前验证本机私钥文件。不会记录、缓存或显示私钥内容。
/// </summary>
public sealed class PrivateKeyValidator
{
    public const string FileNotFoundMessage = "私钥文件不存在，请检查路径。";
    public const string FileNotReadableMessage = "无法读取私钥文件，请检查文件权限。";
    public const string InvalidFormatMessage = "私钥文件格式无效，FluentShell 仅支持 OpenSSH 格式的私钥。";
    public const string CorruptedKeyMessage = "私钥文件已损坏或不完整。";
    public const string PassphraseRequiredMessage = "此私钥需要口令，请在下方输入。";

    public Task<PrivateKeyValidationResult> ValidateAsync(
        string? privateKeyPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Validate(privateKeyPath, cancellationToken), cancellationToken);

    private static PrivateKeyValidationResult Validate(
        string? privateKeyPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(privateKeyPath))
            return Invalid(FileNotFoundMessage);

        try
        {
            // 使用同一个已打开的流完成可读性和解析检查，避免检查后文件被替换。
            using var keyStream = new FileStream(
                privateKeyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!HasSupportedPrivateKeyHeader(keyStream))
                return Invalid(InvalidFormatMessage);
            cancellationToken.ThrowIfCancellationRequested();

            var requiresPassphrase = SshConnectionService.RequiresPrivateKeyPassphrase(keyStream);
            cancellationToken.ThrowIfCancellationRequested();

            return requiresPassphrase
                ? new PrivateKeyValidationResult(true, PassphraseRequiredMessage, true)
                : new PrivateKeyValidationResult(true, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return Invalid(FileNotFoundMessage);
        }
        catch (DirectoryNotFoundException)
        {
            return Invalid(FileNotFoundMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(FileNotReadableMessage);
        }
        catch (SecurityException)
        {
            return Invalid(FileNotReadableMessage);
        }
        catch (PathTooLongException)
        {
            return Invalid(FileNotFoundMessage);
        }
        catch (ArgumentException)
        {
            return Invalid(FileNotFoundMessage);
        }
        catch (NotSupportedException)
        {
            return Invalid(FileNotFoundMessage);
        }
        catch (EndOfStreamException)
        {
            return Invalid(CorruptedKeyMessage);
        }
        catch (InvalidDataException)
        {
            return Invalid(CorruptedKeyMessage);
        }
        catch (IOException)
        {
            return Invalid(FileNotReadableMessage);
        }
        catch (FormatException)
        {
            return Invalid(CorruptedKeyMessage);
        }
        catch (InvalidCipherTextException)
        {
            return Invalid(CorruptedKeyMessage);
        }
        catch (SshException)
        {
            return Invalid(CorruptedKeyMessage);
        }
    }

    private static PrivateKeyValidationResult Invalid(string message) =>
        new(false, message, false);

    internal static bool HasSupportedPrivateKeyHeader(Stream stream)
    {
        var originalPosition = stream.Position;
        try
        {
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 256,
                leaveOpen: true);
            return reader.ReadLine() switch
            {
                "-----BEGIN OPENSSH PRIVATE KEY-----" => true,
                "-----BEGIN RSA PRIVATE KEY-----" => true,
                "-----BEGIN EC PRIVATE KEY-----" => true,
                "-----BEGIN PRIVATE KEY-----" => true,
                "-----BEGIN ENCRYPTED PRIVATE KEY-----" => true,
                _ => false
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}
