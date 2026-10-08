using System.Text;
using FluentShell.Models;

namespace FluentShell.Tests;

[TestClass]
public sealed class TextFileDocumentTests
{
    [TestMethod]
    [DataRow("utf8")]
    [DataRow("utf8-bom")]
    [DataRow("utf16-le")]
    [DataRow("utf16-be")]
    [DataRow("utf32-le")]
    [DataRow("utf32-be")]
    public void Editing_preserves_encoding_bom_and_crlf(string format)
    {
        Encoding encoding = format switch
        {
            "utf8" => new UTF8Encoding(false, true),
            "utf8-bom" => new UTF8Encoding(true, true),
            "utf16-le" => new UnicodeEncoding(false, true, true),
            "utf16-be" => new UnicodeEncoding(true, true, true),
            "utf32-le" => new UTF32Encoding(false, true, true),
            _ => new UTF32Encoding(true, true, true)
        };
        byte[] Bytes(string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        var original = Bytes("中文 😀\r\n第二行\r\n");
        var document = TextFileDocument.Decode(original);
        Assert.AreEqual("中文 😀\n第二行\n", document.Text);
        Assert.IsFalse(document.IsModified("中文 😀\r第二行\r"));
        var saved = document.Encode("中文 😀\r已编辑\r");
        CollectionAssert.AreEqual(Bytes("中文 😀\r\n已编辑\r\n"), saved);
        CollectionAssert.AreEqual(original, document.OriginalBytes.ToArray());
        Assert.IsTrue(document.IsModified("中文 😀\r已编辑\r"));
        document.AcceptSaved("中文 😀\r已编辑\r", saved);
        Assert.IsFalse(document.IsModified("中文 😀\n已编辑\n"));
        CollectionAssert.AreEqual(saved, document.OriginalBytes.ToArray());
    }

    [TestMethod]
    [DataRow("\n", "LF")]
    [DataRow("\r", "CR")]
    [DataRow("\r\n", "CRLF")]
    public void Newline_style_survives_textbox_normalization(string newline, string label)
    {
        var document = TextFileDocument.Decode(Encoding.UTF8.GetBytes("a" + newline + "b"));
        Assert.AreEqual(label, document.NewLineLabel);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("a" + newline + "c"), document.Encode("a\rc"));
    }

    [TestMethod]
    public void Unchanged_mixed_newlines_roundtrip_without_modification()
    {
        var original = Encoding.UTF8.GetBytes("one\r\ntwo\nthree\r");
        var document = TextFileDocument.Decode(original);
        Assert.IsTrue(document.HasMixedNewLines);
        CollectionAssert.AreEqual(original, document.Encode(document.Text));
    }

    [TestMethod]
    public void Empty_file_is_editable_as_utf8_without_bom()
    {
        var document = TextFileDocument.Decode([]);
        Assert.AreEqual(string.Empty, document.Text);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("新内容\n"), document.Encode("新内容\r"));
    }

    [TestMethod]
    public void Binary_and_invalid_utf8_are_rejected()
    {
        foreach (var bytes in new byte[][] { [0, 1, 2], [0xff, 0xab], [0x61, 0x1b, 0x62], [0xff, 0xfe, 0x61] })
            Assert.ThrowsExactly<IOException>(() => TextFileDocument.Decode(bytes));
    }

    [TestMethod]
    public void Read_and_encoded_output_are_bounded_by_bytes()
    {
        Assert.ThrowsExactly<IOException>(() => TextFileDocument.Decode(new byte[TextFileDocument.MaximumBytes + 1]));
        var document = TextFileDocument.Decode([]);
        Assert.ThrowsExactly<IOException>(() => document.Encode(new string('中', TextFileDocument.MaximumBytes / 3 + 1)));
        Assert.IsFalse(document.IsModified(string.Empty));
    }
}
