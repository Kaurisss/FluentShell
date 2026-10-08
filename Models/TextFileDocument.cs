using System.Text;

namespace FluentShell.Models;

/// <summary>文本编辑的原始字节、编码及换行约定；内容从不写入诊断日志。</summary>
public sealed class TextFileDocument
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private byte[] _originalBytes;

    private TextFileDocument(byte[] bytes, Encoding encoding, byte[] preamble, string text)
    {
        _originalBytes = bytes.ToArray();
        _encoding = encoding;
        _preamble = preamble;
        Text = NormalizeNewLines(text);
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        NewLine = crlf > 0 && crlf >= lf && crlf >= cr ? "\r\n" : cr > lf ? "\r" : "\n";
        HasMixedNewLines = new[] { crlf, lf, cr }.Count(count => count > 0) > 1;
    }

    public string Text { get; private set; }
    public string NewLine { get; }
    public bool HasMixedNewLines { get; private set; }
    public string NewLineLabel => NewLine == "\r\n" ? "CRLF" : NewLine == "\r" ? "CR" : "LF";
    public string EncodingLabel => _encoding.WebName.ToUpperInvariant() + (_preamble.Length > 0 ? " · BOM" : "");
    public ReadOnlyMemory<byte> OriginalBytes => _originalBytes;

    public static TextFileDocument Decode(byte[] bytes)
    {
        EnsureSize(bytes.Length);
        Encoding encoding = new UTF8Encoding(false, true);
        byte[] preamble = [];
        // UTF-32 LE shares the first two bytes with UTF-16 LE; inspect it first.
        foreach (var candidate in new Encoding[]
        {
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)
        })
        {
            var marker = candidate.GetPreamble();
            if (!bytes.AsSpan().StartsWith(marker)) continue;
            encoding = candidate;
            preamble = marker;
            break;
        }

        string text;
        try { text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length); }
        catch (DecoderFallbackException)
        {
            throw new IOException("文件不是有效的 UTF-8 文本或带 BOM 的 UTF-16/UTF-32 文本，无法安全编辑。");
        }
        if (text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            throw new IOException("文件包含二进制内容，无法使用文本编辑器打开。");
        return new TextFileDocument(bytes, encoding, preamble, text);
    }

    public bool IsModified(string text) => !string.Equals(Text, NormalizeNewLines(text), StringComparison.Ordinal);

    public byte[] Encode(string text)
    {
        var normalized = NormalizeNewLines(text);
        // Preserve even mixed newline sequences byte for byte if nothing changed.
        if (normalized == Text) return _originalBytes.ToArray();
        var serialized = normalized.Replace("\n", NewLine, StringComparison.Ordinal);
        int length;
        try { length = checked(_encoding.GetByteCount(serialized) + _preamble.Length); }
        catch (EncoderFallbackException) { throw new IOException("文本包含无效的 Unicode 字符，无法保存。"); }
        EnsureSize(length);
        var bytes = new byte[length];
        _preamble.CopyTo(bytes, 0);
        _encoding.GetBytes(serialized, 0, serialized.Length, bytes, _preamble.Length);
        return bytes;
    }

    public void AcceptSaved(string text, byte[] bytes)
    {
        Text = NormalizeNewLines(text);
        _originalBytes = bytes.ToArray();
        HasMixedNewLines = false;
    }

    public static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static void EnsureSize(long length)
    {
        if (length > MaximumBytes) throw new IOException("文本编辑器仅支持不超过 2 MB 的文件。");
    }
}
