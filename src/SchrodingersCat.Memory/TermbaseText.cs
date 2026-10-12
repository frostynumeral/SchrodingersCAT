using System.Buffers.Binary;

namespace SchrodingersCat.Memory;

/// <summary>
/// How the termbase stores its strings: raw UTF-16, little-endian, no header, so nothing in them is lost (lone
/// surrogates, U+0000, U+FFFF), which a UTF-8 <c>TEXT</c> column would replace. <c>NULL</c> is null; an empty BLOB is
/// the empty string.
/// </summary>
internal static class TermbaseText
{
    public static byte[] Write(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), text[i]);
        return bytes;
    }

    public static object WriteNullable(string? text) => text is null ? DBNull.Value : Write(text);

    /// <summary>A stored string, as a column's value is read (<see cref="SqliteDataReader.GetValue"/>).</summary>
    /// <exception cref="TermbaseFormatException">The value isn't a BLOB of whole UTF-16 code units.</exception>
    public static string Decode(object value)
    {
        if (value is not byte[] bytes || bytes.Length % 2 != 0)
            throw new TermbaseFormatException("A stored string is damaged.");

        return string.Create(bytes.Length / 2, bytes, static (chars, bytes) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        });
    }

    /// <summary>A stored string that may be null (<c>NULL</c>).</summary>
    public static string? DecodeNullable(object value) => value is DBNull ? null : Decode(value);
}
