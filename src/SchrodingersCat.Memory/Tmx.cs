using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Memory;

/// <summary>What a TMX import found: the entries for the language pair, and why the rest were skipped.</summary>
public sealed record TmxImport(IReadOnlyList<TmEntry> Entries, IReadOnlyList<string> Skipped);

/// <summary>What a TMX export wrote, and why entries were skipped.</summary>
public sealed record TmxExport(int Written, IReadOnlyList<string> Skipped);

/// <summary>
/// TMX 1.4b import and export (design section 5), the format every CAT tool reads and writes.
/// </summary>
/// <remarks>
/// <para><b>Codes:</b> a pair within a segment is <c>&lt;bpt&gt;</c>/<c>&lt;ept&gt;</c> (matched by <c>i</c>), a half
/// whose partner is in another segment is <c>&lt;it&gt;</c>, a standalone code <c>&lt;ph&gt;</c>. The <c>x</c>
/// attribute ties a code in the source to the same code in the target; on import it becomes the code id, so code
/// transfer can map them. The native code inside the elements isn't kept: entries have no original data, and a match's
/// codes are replaced by the current segment's anyway. <c>&lt;hi&gt;</c> keeps its text; <c>&lt;ut&gt;</c> is
/// dropped.</para>
/// <para><b>Untrusted input:</b> TMX files come from anywhere, so reading has the XLIFF reader's defences: no DTDs or
/// external entities, a size limit and a nesting limit. A file that isn't TMX fails with
/// <see cref="TmFormatException"/>; a translation unit it can't use is skipped and reported, so one bad unit
/// doesn't lose the rest.</para>
/// <para><b>Languages:</b> a <c>&lt;tuv&gt;</c> serves a language if its <c>xml:lang</c> is the same (ignoring
/// case), or a regional variant of it: a <c>de</c> TM takes <c>de-DE</c> and <c>de-AT</c> (decision 3).</para>
/// </remarks>
public static class Tmx
{
    /// <summary>The default size limit for imports.</summary>
    public const long DefaultMaxInputBytes = 512L * 1024 * 1024;

    /// <summary>The nesting limit for imports.</summary>
    public const int MaxElementDepth = 64;

    private static readonly XNamespace XmlNs = XNamespace.Xml;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false,
    };

    // ---------------------------------------------------------------- import

    public static async Task<TmxImport> ReadAsync(
        Stream input,
        string sourceLanguage,
        string targetLanguage,
        long maxInputBytes = DefaultMaxInputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        var entries = new List<TmEntry>();
        var skipped = new List<string>();
        try
        {
            using var reader = new DepthLimitingXmlReader(
                XmlReader.Create(new LimitedReadStream(input, maxInputBytes, Refused), ReaderSettings), MaxElementDepth, Refused);

            await reader.MoveToContentAsync();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "tmx" || reader.NamespaceURI.Length != 0)
                throw new TmFormatException("The file isn't TMX: its root element isn't <tmx>.");

            var number = 0;
            await reader.ReadAsync();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "tu" && reader.NamespaceURI.Length == 0)
                {
                    number++;
                    var tu = (XElement)await XNode.ReadFromAsync(reader, cancellationToken);
                    try
                    {
                        entries.Add(ReadUnit(tu, sourceLanguage, targetLanguage));
                    }
                    catch (SkipException ex)
                    {
                        skipped.Add($"Translation unit {number}: {ex.Message}");
                    }
                }
                else
                {
                    await reader.ReadAsync();
                }
            }
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.Ordinal))
        {
            throw new TmFormatException("The file contains a DTD (<!DOCTYPE>), which is refused: it could expand entities or reference external files.", ex);
        }
        catch (XmlException ex)
        {
            throw new TmFormatException($"The file is not well-formed XML: {ex.Message}", ex);
        }

        return new TmxImport(entries, skipped);
    }

    private static TmFormatException Refused(string message) => new(message);

    private static TmEntry ReadUnit(XElement tu, string sourceLanguage, string targetLanguage)
    {
        var tuvs = tu.Elements("tuv").ToList();
        var source = Variant(tuvs, sourceLanguage) ?? throw new SkipException($"no {sourceLanguage} text.");
        var target = Variant(tuvs, targetLanguage) ?? throw new SkipException($"no {targetLanguage} text.");
        var props = tu.Elements("prop").ToList();

        return new TmEntry(ReadSegment(source), ReadSegment(target))
        {
            PreviousSource = Prop(props, "x-context-pre"),
            NextSource = Prop(props, "x-context-post"),
            Document = Prop(props, "x-document"),
            Changed = Date((string?)tu.Attribute("changedate") ?? (string?)tu.Attribute("creationdate")),
            ChangedBy = (string?)tu.Attribute("changeid") ?? (string?)tu.Attribute("creationid"),
        };
    }

    // The exact language first, then a regional variant of it.
    private static XElement? Variant(List<XElement> tuvs, string language)
    {
        static string? Lang(XElement tuv) => (string?)tuv.Attribute(XmlNs + "lang") ?? (string?)tuv.Attribute("lang");

        return tuvs.FirstOrDefault(t => string.Equals(Lang(t), language, StringComparison.OrdinalIgnoreCase))
            ?? tuvs.FirstOrDefault(t => Lang(t) is { } lang && TmLanguages.Serves(lang, language));
    }

    private static string? Prop(List<XElement> props, string type) =>
        props.FirstOrDefault(p => string.Equals((string?)p.Attribute("type"), type, StringComparison.OrdinalIgnoreCase))?.Value;

    private static DateTimeOffset Date(string? value) =>
        DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : default;

    private static InlineContent ReadSegment(XElement tuv)
    {
        var seg = tuv.Element("seg") ?? throw new SkipException("a <tuv> has no <seg>.");
        var elements = new List<InlineElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var openPairs = new Dictionary<string, string>(StringComparer.Ordinal); // i → id
        var next = 0;

        // x ties a code to the same code in the other language; without it, codes are numbered in order.
        string NewId(XElement code)
        {
            var id = (string?)code.Attribute("x") ?? $"n{++next}";
            while (!ids.Add(id))
                id += "'";
            return id;
        }

        void Read(XElement parent)
        {
            foreach (var node in parent.Nodes())
            {
                if (node is XText text)
                {
                    elements.Add(new InlineText(text.Value));
                    continue;
                }

                if (node is not XElement element)
                    continue;

                switch (element.Name.LocalName)
                {
                    case "bpt":
                        {
                            var i = (string?)element.Attribute("i") ?? throw new SkipException("a <bpt> has no i.");
                            var id = NewId(element);
                            if (!openPairs.TryAdd(i, id))
                                throw new SkipException($"two <bpt>s have i=\"{i}\".");
                            elements.Add(new InlineCode(id, InlineCodeKind.Opening));
                            break;
                        }

                    case "ept":
                        {
                            var i = (string?)element.Attribute("i") ?? throw new SkipException("an <ept> has no i.");
                            if (!openPairs.Remove(i, out var id))
                                throw new SkipException($"<ept i=\"{i}\"> has no <bpt> before it.");
                            elements.Add(new InlineCode(id, InlineCodeKind.Closing));
                            break;
                        }

                    case "it":
                        elements.Add(new InlineCode(NewId(element), (string?)element.Attribute("pos") switch
                        {
                            "begin" => InlineCodeKind.Opening,
                            "end" => InlineCodeKind.Closing,
                            var pos => throw new SkipException($"<it pos=\"{pos}\"> isn't begin or end."),
                        }));
                        break;
                    case "ph":
                        elements.Add(new InlineCode(NewId(element), InlineCodeKind.Standalone));
                        break;
                    case "hi":
                        Read(element);
                        break;
                    case "ut":
                        break;
                    default:
                        throw new SkipException($"<{element.Name.LocalName}> isn't allowed in a <seg>.");
                }
            }
        }

        Read(seg);
        return new InlineContent(elements);
    }

    private sealed class SkipException(string message) : Exception(message);

    // ---------------------------------------------------------------- export

    public static async Task<TmxExport> WriteAsync(
        Stream output,
        IEnumerable<TmEntry> entries,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineHandling = NewLineHandling.Entitize,
            CloseOutput = false,
        };

        var written = 0;
        var skipped = new List<string>();
        await using var writer = XmlWriter.Create(output, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "tmx", null);
        await writer.WriteAttributeStringAsync(null, "version", null, "1.4");
        await writer.WriteStartElementAsync(null, "header", null);
        foreach (var (name, value) in new[]
        {
            ("creationtool", "SchrodingersCAT"), ("creationtoolversion", "1"), ("segtype", "sentence"),
            ("o-tmf", "SchrodingersCAT"), ("adminlang", "en"), ("srclang", sourceLanguage), ("datatype", "xml"),
        })
            await writer.WriteAttributeStringAsync(null, name, null, value);
        await writer.WriteEndElementAsync();
        await writer.WriteStartElementAsync(null, "body", null);

        var number = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;

            // XML can't hold every string content can (e.g. lone surrogates from <cp>); check before writing.
            if (UnwritableText(entry) is { } problem)
            {
                skipped.Add($"Entry {number}: {problem}");
                continue;
            }

            await Unit(entry, sourceLanguage, targetLanguage).WriteToAsync(writer, cancellationToken);
            written++;
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        return new TmxExport(written, skipped);
    }

    private static XElement Unit(TmEntry entry, string sourceLanguage, string targetLanguage)
    {
        var tu = new XElement("tu");
        if (entry.Changed != default)
            tu.Add(new XAttribute("changedate", entry.Changed.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
        if (entry.ChangedBy is not null)
            tu.Add(new XAttribute("changeid", entry.ChangedBy));
        foreach (var (type, value) in new[] { ("x-context-pre", entry.PreviousSource), ("x-context-post", entry.NextSource), ("x-document", entry.Document) })
        {
            if (value is not null)
                tu.Add(new XElement("prop", new XAttribute("type", type), value));
        }

        // x numbers each code id, so the same code in source and target has the same x.
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        tu.Add(Variant(entry.Source, sourceLanguage, numbers), Variant(entry.Target, targetLanguage, numbers));
        return tu;
    }

    private static XElement Variant(InlineContent content, string language, Dictionary<string, int> numbers)
    {
        // Start with an empty text node, so the writer's indentation can't add whitespace to the segment.
        var seg = new XElement("seg", new XText(""));
        var pairs = new Dictionary<string, int>(StringComparer.Ordinal); // open pair id → i
        var lastI = 0; // i is never reused within a segment, even after its pair closes
        for (var index = 0; index < content.Count; index++)
        {
            var element = content[index];
            switch (element)
            {
                case InlineText text:
                    seg.Add(text.Text);
                    break;
                case InlineCode code:
                    var x = numbers.TryGetValue(code.Id, out var n) ? n : numbers[code.Id] = numbers.Count + 1;
                    seg.Add(code.Kind switch
                    {
                        InlineCodeKind.Standalone => new XElement("ph", new XAttribute("x", x)),
                        InlineCodeKind.Opening when !pairs.ContainsKey(code.Id) && ClosesLater(content, index, code.Id) =>
                            new XElement("bpt", new XAttribute("i", pairs[code.Id] = ++lastI), new XAttribute("x", x)),
                        InlineCodeKind.Closing when pairs.Remove(code.Id, out var i) => new XElement("ept", new XAttribute("i", i)),
                        _ => new XElement("it", new XAttribute("pos", code.Kind == InlineCodeKind.Opening ? "begin" : "end"), new XAttribute("x", x)),
                    });
                    break;
            }
        }

        return new XElement("tuv", new XAttribute(XmlNs + "lang", language), seg);
    }

    // A <bpt> needs its <ept> later in the same segment; otherwise the half is an <it>.
    private static bool ClosesLater(InlineContent content, int index, string id) =>
        content.Skip(index + 1).Any(e => e is InlineCode { Kind: InlineCodeKind.Closing } c && c.Id == id);

    private static string? UnwritableText(TmEntry entry)
    {
        var texts = entry.Source.Concat(entry.Target).OfType<InlineText>().Select(t => t.Text)
            .Concat(new[] { entry.PreviousSource, entry.NextSource, entry.Document, entry.ChangedBy }.OfType<string>());
        foreach (var text in texts)
        {
            try
            {
                XmlConvert.VerifyXmlChars(text);
            }
            catch (XmlException)
            {
                return "its text has characters XML can't hold.";
            }
        }

        return null;
    }
}
