using System.Buffers.Binary;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>
/// The binary form of <see cref="InlineContent"/> in a TM file: every property of every element, so storing loses
/// nothing. Strings are raw UTF-16, because content can hold code points (lone surrogates, from <c>&lt;cp&gt;</c>)
/// that UTF-8 text columns or JSON would replace or reject.
/// </summary>
/// <remarks>A TM file can come from someone else, so <see cref="Read"/> validates everything and throws only
/// <see cref="TmFormatException"/>.</remarks>
internal static class TmContentFormat
{
    private const byte Version = 1;
    private const byte TextTag = 0;
    private const byte CodeTag = 1;
    private const byte MarkerTag = 2;

    public static byte[] Write(InlineContent content)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Version);
            writer.Write7BitEncodedInt(content.Count);
            foreach (var element in content)
            {
                switch (element)
                {
                    case InlineText text:
                        writer.Write(TextTag);
                        WriteString(writer, text.Text);
                        break;
                    case InlineCode code:
                        writer.Write(CodeTag);
                        WriteString(writer, code.Id);
                        writer.Write((byte)code.Kind);
                        foreach (var value in new[] { code.DataRef, code.Type, code.SubType, code.Disp, code.Equiv, code.CopyOf, code.SubFlows, code.Dir })
                            WriteString(writer, value);
                        writer.Write((byte)((code.CanCopy ? 1 : 0) | (code.CanDelete ? 2 : 0) | (code.IsIsolated ? 4 : 0)
                            | (code.CanOverlap is null ? 0 : 8) | (code.CanOverlap == true ? 16 : 0)));
                        writer.Write((byte)code.CanReorder);
                        WriteAttributes(writer, code.ExtensionAttributes);
                        break;
                    case InlineMarker marker:
                        writer.Write(MarkerTag);
                        WriteString(writer, marker.Id);
                        writer.Write((byte)marker.Kind);
                        writer.Write((byte)((marker.Translate is null ? 0 : 1) | (marker.Translate == true ? 2 : 0)));
                        WriteString(writer, marker.Type);
                        WriteString(writer, marker.Ref);
                        WriteString(writer, marker.Value);
                        WriteAttributes(writer, marker.ExtensionAttributes);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown inline element {element.GetType().Name}.");
                }
            }
        }

        return stream.ToArray();
    }

    public static InlineContent Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false));
            if (reader.ReadByte() != Version)
                throw new TmFormatException("The stored content is from an unknown version.");

            var count = Count(reader);
            var elements = new List<InlineElement>(count);
            for (var i = 0; i < count; i++)
                elements.Add(ReadElement(reader));
            if (reader.BaseStream.Position != data.Length)
                throw new TmFormatException("The stored content has data after its end.");
            return new InlineContent(elements);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or ArgumentException)
        {
            throw new TmFormatException("The stored content is damaged.", ex);
        }
    }

    private static InlineElement ReadElement(BinaryReader reader)
    {
        switch (reader.ReadByte())
        {
            case TextTag:
                return new InlineText(ReadString(reader) ?? throw Damaged());
            case CodeTag:
                {
                    var id = ReadString(reader) ?? throw Damaged();
                    var kind = Enum<InlineCodeKind>(reader.ReadByte());
                    var values = new string?[8];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = ReadString(reader);
                    var flags = reader.ReadByte();
                    if (flags > 31)
                        throw Damaged();
                    return new InlineCode(id, kind)
                    {
                        DataRef = values[0],
                        Type = values[1],
                        SubType = values[2],
                        Disp = values[3],
                        Equiv = values[4],
                        CopyOf = values[5],
                        SubFlows = values[6],
                        Dir = values[7],
                        CanCopy = (flags & 1) != 0,
                        CanDelete = (flags & 2) != 0,
                        IsIsolated = (flags & 4) != 0,
                        CanOverlap = (flags & 8) == 0 ? null : (flags & 16) != 0,
                        CanReorder = Enum<CanReorder>(reader.ReadByte()),
                        ExtensionAttributes = ReadAttributes(reader),
                    };
                }

            case MarkerTag:
                {
                    var id = ReadString(reader) ?? throw Damaged();
                    var kind = Enum<InlineMarkerKind>(reader.ReadByte());
                    var flags = reader.ReadByte();
                    if (flags > 3)
                        throw Damaged();
                    return new InlineMarker(id, kind)
                    {
                        Translate = (flags & 1) == 0 ? null : (flags & 2) != 0,
                        Type = ReadString(reader),
                        Ref = ReadString(reader),
                        Value = ReadString(reader),
                        ExtensionAttributes = ReadAttributes(reader),
                    };
                }

            default:
                throw Damaged();
        }
    }

    private static void WriteAttributes(BinaryWriter writer, IReadOnlyList<ExtensionAttr> attributes)
    {
        writer.Write7BitEncodedInt(attributes.Count);
        foreach (var attribute in attributes)
        {
            WriteString(writer, attribute.Namespace);
            WriteString(writer, attribute.LocalName);
            WriteString(writer, attribute.Value);
        }
    }

    private static List<ExtensionAttr> ReadAttributes(BinaryReader reader)
    {
        var count = Count(reader);
        var attributes = new List<ExtensionAttr>(count);
        for (var i = 0; i < count; i++)
            attributes.Add(new ExtensionAttr(ReadString(reader) ?? throw Damaged(), ReadString(reader) ?? throw Damaged(), ReadString(reader) ?? throw Damaged()));
        return attributes;
    }

    // Null as 0, otherwise the length plus one, then the UTF-16 code units, little-endian.
    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write7BitEncodedInt(0);
            return;
        }

        writer.Write7BitEncodedInt(value.Length + 1);
        Span<byte> unit = stackalloc byte[2];
        foreach (var c in value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(unit, c);
            writer.Write(unit);
        }
    }

    private static string? ReadString(BinaryReader reader)
    {
        var length = Count(reader) - 1;
        if (length < 0)
            return null;
        var bytes = reader.ReadBytes(length * 2);
        if (bytes.Length != length * 2)
            throw Damaged();
        return string.Create(length, bytes, (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(i * 2));
        });
    }

    // A count can't exceed what's left to read, so damaged data can't make us allocate a huge list.
    private static int Count(BinaryReader reader)
    {
        var count = reader.Read7BitEncodedInt();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position + 1)
            throw Damaged();
        return count;
    }

    private static T Enum<T>(byte value)
        where T : struct, Enum =>
        System.Enum.IsDefined(typeof(T), (int)value) ? (T)(object)(int)value : throw Damaged();

    private static TmFormatException Damaged() => new("The stored content is damaged.");
}
