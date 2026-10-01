using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Xliff;

/// <summary>
/// XLIFF 2.0 / 2.1 / 2.2 core serializer.
/// </summary>
/// <remarks>
/// Current limitations (intentional for the first iteration):
/// <list type="bullet">
/// <item>Inline codes (<c>&lt;ph&gt;</c>, <c>&lt;pc&gt;</c>, <c>&lt;mrk&gt;</c>, …), <c>&lt;cp&gt;</c> and
/// <c>&lt;originalData&gt;</c> are read, but the writer does not support them yet and throws a
/// <see cref="NotSupportedException"/> rather than dropping them.</item>
/// <item><c>&lt;group&gt;</c> hierarchy is flattened, and modules/extensions (matches, glossary, …) are not
/// preserved: the writer regenerates the file from the model.</item>
/// </list>
/// </remarks>
public sealed class Xliff20Serializer : IXliffSerializer
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:document:2.0";

    private static readonly XName XmlSpace = XNamespace.Xml + "space";

    // ---------------------------------------------------------------- reading

    public async Task<TranslationDocument> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        XDocument xml;
        try
        {
            // Whitespace inside <source>/<target> is part of the translatable content.
            xml = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (XmlException ex)
        {
            throw new XliffFormatException($"The file is not well-formed XML: {ex.Message}", ex);
        }

        var root = xml.Root;
        if (root is null || root.Name.LocalName != "xliff")
            throw new XliffFormatException("The root element is not <xliff>.");

        var version = (string?)root.Attribute("version");
        if (root.Name.Namespace != Ns || version is null || !version.StartsWith("2.", StringComparison.Ordinal))
            throw new XliffFormatException($"Unsupported XLIFF version '{version ?? "unknown"}'. Only XLIFF 2.x is supported.");

        var document = new TranslationDocument
        {
            SourceLanguage = RequiredAttribute(root, "srcLang"),
            TargetLanguage = (string?)root.Attribute("trgLang"),
        };

        foreach (var fileElement in root.Elements(Ns + "file"))
            document.Files.Add(ReadFile(fileElement));

        return document;
    }

    private static TranslationFile ReadFile(XElement fileElement)
    {
        var file = new TranslationFile
        {
            Id = RequiredAttribute(fileElement, "id"),
            Original = (string?)fileElement.Attribute("original"),
        };

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // Units may sit inside (nested) <group>s; units themselves never nest, so Descendants is safe.
        foreach (var unitElement in fileElement.Descendants(Ns + "unit"))
        {
            var unit = ReadUnit(unitElement);
            if (!seenIds.Add(unit.Id))
                throw new XliffFormatException($"Duplicate unit id '{unit.Id}' in file '{file.Id}'.");
            file.Units.Add(unit);
        }

        return file;
    }

    private static TranslationUnit ReadUnit(XElement unitElement)
    {
        var unit = new TranslationUnit
        {
            Id = RequiredAttribute(unitElement, "id"),
            Name = (string?)unitElement.Attribute("name"),
        };

        var notesElement = unitElement.Element(Ns + "notes");
        if (notesElement is not null)
        {
            foreach (var noteElement in notesElement.Elements(Ns + "note"))
                unit.Notes.Add(ReadNote(noteElement));
        }

        var originalDataElement = unitElement.Element(Ns + "originalData");
        if (originalDataElement is not null)
            ReadOriginalData(originalDataElement, unit);

        var segmentIds = ReadSegmentIds(unitElement, unit.Id);
        var source = new InlineSide("source", unit.Id, unit.OriginalData, segmentIds);
        var target = new InlineSide("target", unit.Id, unit.OriginalData, segmentIds);

        // Segments and ignorables are siblings and their order matters (it rebuilds the full text).
        foreach (var child in unitElement.Elements())
        {
            if (child.Name == Ns + "segment")
                unit.Segments.Add(ReadSegment(child, source, target, isIgnorable: false));
            else if (child.Name == Ns + "ignorable")
                unit.Segments.Add(ReadSegment(child, source, target, isIgnorable: true));
        }

        source.VerifyPairsClosed();
        target.VerifyPairsClosed();

        if (!unit.TranslatableSegments.Any())
            throw new XliffFormatException($"Unit '{unit.Id}' contains no <segment>.");

        return unit;
    }

    // Segment, ignorable and inline ids share one id space per unit.
    private static HashSet<string> ReadSegmentIds(XElement unitElement, string unitId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in unitElement.Elements())
        {
            if (child.Name != Ns + "segment" && child.Name != Ns + "ignorable")
                continue;

            var id = (string?)child.Attribute("id");
            if (id is not null && !ids.Add(id))
                throw new XliffFormatException($"Duplicate segment id '{id}' in unit '{unitId}'.");
        }

        return ids;
    }

    private static void ReadOriginalData(XElement element, TranslationUnit unit)
    {
        foreach (var dataElement in element.Elements())
        {
            if (dataElement.Name != Ns + "data")
                throw new XliffFormatException($"Unit '{unit.Id}' contains <{dataElement.Name.LocalName}> inside <originalData>, which is not allowed.");

            var attributes = new InlineAttributes(dataElement, unit.Id);
            var id = attributes.Required("id");
            attributes.Optional(XmlSpace); // the text is read verbatim anyway
            attributes.VerifyNoneLeft();

            // <data> holds the native code as text, with characters that are invalid in XML as <cp>.
            var text = new StringBuilder();
            foreach (var node in dataElement.Nodes())
            {
                if (node is XText textNode)
                    text.Append(textNode.Value);
                else if (node is XElement child && child.Name == Ns + "cp")
                    text.Append(ReadCodePoint(child, unit.Id));
                else if (node is XElement other)
                    throw new XliffFormatException($"Unit '{unit.Id}' contains <{other.Name.LocalName}> inside <data>, which is not allowed.");
            }

            if (!unit.OriginalData.TryAdd(id, text.ToString()))
                throw new XliffFormatException($"Duplicate <data> id '{id}' in unit '{unit.Id}'.");
        }
    }

    private static Segment ReadSegment(XElement element, InlineSide source, InlineSide target, bool isIgnorable)
    {
        var unitId = source.UnitId;
        var sourceElement = element.Element(Ns + "source")
            ?? throw new XliffFormatException($"A segment in unit '{unitId}' has no <source>.");
        var targetElement = element.Element(Ns + "target");

        return new Segment
        {
            Id = (string?)element.Attribute("id"),
            Source = ReadContent(sourceElement, source),
            Target = targetElement is null ? null : ReadContent(targetElement, target),
            State = isIgnorable ? SegmentState.Initial : ParseState((string?)element.Attribute("state"), unitId),
            SubState = (string?)element.Attribute("subState"),
            IsIgnorable = isIgnorable,
        };
    }

    private static Note ReadNote(XElement element)
    {
        var priorityText = (string?)element.Attribute("priority");
        int? priority = null;
        if (priorityText is not null)
        {
            if (!int.TryParse(priorityText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 10)
                throw new XliffFormatException($"Invalid note priority '{priorityText}' (must be 1-10).");
            priority = p;
        }

        return new Note
        {
            Id = (string?)element.Attribute("id"),
            Text = element.Value,
            Category = (string?)element.Attribute("category"),
            Priority = priority,
            AppliesTo = (string?)element.Attribute("appliesTo") switch
            {
                null => null,
                "source" => NoteAppliesTo.Source,
                "target" => NoteAppliesTo.Target,
                var other => throw new XliffFormatException($"Invalid note appliesTo value '{other}'."),
            },
        };
    }

    private static InlineContent ReadContent(XElement element, InlineSide side)
    {
        var elements = new List<InlineElement>();
        ReadInline(element, side, elements);
        return new InlineContent(elements);
    }

    // Flattens the inline tree: <pc> and <mrk> become an opening element, their content and a closing element.
    private static void ReadInline(XElement parent, InlineSide side, List<InlineElement> elements)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text)
                elements.Add(new InlineText(text.Value));
            else if (node is XElement child)
                ReadInlineElement(child, side, elements);
        }
    }

    private static void ReadInlineElement(XElement element, InlineSide side, List<InlineElement> elements)
    {
        var name = element.Name.Namespace == Ns ? element.Name.LocalName : null;
        if (name is not ("ph" or "pc" or "sc" or "ec" or "mrk" or "sm" or "em" or "cp"))
            throw new XliffFormatException(
                $"Unit '{side.UnitId}' contains <{element.Name.LocalName}> in its {side.Name}, which is not allowed in content.");
        if (name is not ("pc" or "mrk"))
            RequireEmpty(element, side.UnitId);

        var a = new InlineAttributes(element, side.UnitId);
        switch (name)
        {
            case "ph":
            {
                var id = side.DefineId(a.Required("id"));
                elements.Add(ReadPairLevel(new InlineCode(id, InlineCodeKind.Standalone), a) with
                {
                    DataRef = side.DataRef(a.Optional("dataRef")),
                    Disp = a.Optional("disp"),
                    Equiv = a.Optional("equiv"),
                    SubFlows = a.Optional("subFlows"),
                    ExtensionAttributes = a.ExtensionAttributes(),
                });
                break;
            }

            case "pc":
            {
                var id = side.DefineId(a.Required("id"));
                var shared = ReadPairLevel(new InlineCode(id, InlineCodeKind.Opening), a) with
                {
                    CanOverlap = a.YesNo("canOverlap") ?? false,
                };
                var opening = shared with
                {
                    Dir = a.Optional("dir"),
                    DataRef = side.DataRef(a.Optional("dataRefStart")),
                    Disp = a.Optional("dispStart"),
                    Equiv = a.Optional("equivStart"),
                    SubFlows = a.Optional("subFlowsStart"),
                };
                var closing = new InlineCode(id, InlineCodeKind.Closing)
                {
                    Type = shared.Type,
                    SubType = shared.SubType,
                    CopyOf = shared.CopyOf,
                    CanCopy = shared.CanCopy,
                    CanDelete = shared.CanDelete,
                    CanOverlap = shared.CanOverlap,
                    // As on <ec>: a pair that starts with firstNo ends with no.
                    CanReorder = shared.CanReorder == CanReorder.FirstNo ? CanReorder.No : shared.CanReorder,
                    DataRef = side.DataRef(a.Optional("dataRefEnd")),
                    Disp = a.Optional("dispEnd"),
                    Equiv = a.Optional("equivEnd"),
                    SubFlows = a.Optional("subFlowsEnd"),
                };

                elements.Add(opening with { ExtensionAttributes = a.ExtensionAttributes() });
                ReadInline(element, side, elements);
                elements.Add(closing);
                break;
            }

            case "sc":
            {
                var id = side.DefineId(a.Required("id"));
                var isolated = a.YesNo("isolated") ?? false;
                if (!isolated)
                    side.OpenCode(id);

                elements.Add(ReadPairLevel(new InlineCode(id, InlineCodeKind.Opening), a) with
                {
                    IsIsolated = isolated,
                    CanOverlap = a.YesNo("canOverlap") ?? true,
                    Dir = a.Optional("dir"),
                    DataRef = side.DataRef(a.Optional("dataRef")),
                    Disp = a.Optional("disp"),
                    Equiv = a.Optional("equiv"),
                    SubFlows = a.Optional("subFlows"),
                    ExtensionAttributes = a.ExtensionAttributes(),
                });
                break;
            }

            case "ec":
            {
                // An isolated <ec> has its own id; otherwise startRef points to its <sc>, and dir is not allowed.
                var isolated = a.YesNo("isolated") ?? false;
                var id = isolated ? side.DefineId(a.Required("id")) : side.CloseCode(a.Required("startRef"));

                elements.Add(ReadPairLevel(new InlineCode(id, InlineCodeKind.Closing), a) with
                {
                    IsIsolated = isolated,
                    CanOverlap = a.YesNo("canOverlap") ?? true,
                    Dir = isolated ? a.Optional("dir") : null,
                    DataRef = side.DataRef(a.Optional("dataRef")),
                    Disp = a.Optional("disp"),
                    Equiv = a.Optional("equiv"),
                    SubFlows = a.Optional("subFlows"),
                    ExtensionAttributes = a.ExtensionAttributes(),
                });
                break;
            }

            case "mrk":
            {
                var id = side.DefineId(a.Required("id"));
                elements.Add(ReadMarker(new InlineMarker(id, InlineMarkerKind.Opening), a));
                ReadInline(element, side, elements);
                elements.Add(new InlineMarker(id, InlineMarkerKind.Closing));
                break;
            }

            case "sm":
            {
                var id = side.DefineId(a.Required("id"));
                side.OpenMarker(id);
                elements.Add(ReadMarker(new InlineMarker(id, InlineMarkerKind.Opening), a));
                break;
            }

            case "em":
            {
                var id = side.CloseMarker(a.Required("startRef"));
                a.VerifyNoneLeft();
                elements.Add(new InlineMarker(id, InlineMarkerKind.Closing));
                break;
            }

            default: // cp
                elements.Add(new InlineText(ReadCodePoint(element, side.UnitId)));
                break;
        }
    }

    private static InlineCode ReadPairLevel(InlineCode code, InlineAttributes a) => code with
    {
        Type = a.Optional("type"),
        SubType = a.Optional("subType"),
        CopyOf = a.Optional("copyOf"),
        CanCopy = a.YesNo("canCopy") ?? true,
        CanDelete = a.YesNo("canDelete") ?? true,
        CanReorder = a.CanReorder() ?? CanReorder.Yes,
    };

    private static InlineMarker ReadMarker(InlineMarker marker, InlineAttributes a) => marker with
    {
        Translate = a.YesNo("translate"),
        Type = a.Optional("type"),
        Ref = a.Optional("ref"),
        Value = a.Optional("value"),
        ExtensionAttributes = a.ExtensionAttributes(),
    };

    private static string ReadCodePoint(XElement element, string unitId)
    {
        RequireEmpty(element, unitId);
        var attributes = new InlineAttributes(element, unitId);
        var hex = attributes.Required("hex");
        attributes.VerifyNoneLeft();

        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint)
            || codePoint is < 0 or > 0x10FFFF)
            throw new XliffFormatException($"Invalid <cp hex=\"{hex}\"> in unit '{unitId}'.");

        // ConvertFromUtf32 rejects surrogates, but <cp> may carry a lone one.
        return codePoint <= 0xFFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);
    }

    private static void RequireEmpty(XElement element, string unitId)
    {
        if (element.Nodes().Any(n => n is not XComment))
            throw new XliffFormatException($"<{element.Name.LocalName}> in unit '{unitId}' must be empty.");
    }

    private static SegmentState ParseState(string? value, string unitId) => value switch
    {
        null or "initial" => SegmentState.Initial,
        "translated" => SegmentState.Translated,
        "reviewed" => SegmentState.Reviewed,
        "final" => SegmentState.Final,
        _ => throw new XliffFormatException($"Invalid segment state '{value}' in unit '{unitId}'."),
    };

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name)
        ?? throw new XliffFormatException($"<{element.Name.LocalName}> is missing the required '{name}' attribute.");

    // ---------------------------------------------------------------- writing

    public async Task WriteAsync(TranslationDocument document, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);

        var root = new XElement(Ns + "xliff",
            new XAttribute("version", "2.0"),
            new XAttribute("srcLang", document.SourceLanguage),
            OptionalAttribute("trgLang", document.TargetLanguage),
            document.Files.Select(WriteFile));

        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            // Safe for our content: XmlWriter stops indenting inside elements that contain text.
            Indent = true,
        };

        await using var writer = XmlWriter.Create(output, settings);
        await xml.SaveAsync(writer, cancellationToken);
    }

    private static XElement WriteFile(TranslationFile file) =>
        new(Ns + "file",
            new XAttribute("id", file.Id),
            OptionalAttribute("original", file.Original),
            file.Units.Select(WriteUnit));

    private static XElement WriteUnit(TranslationUnit unit)
    {
        if (unit.OriginalData.Count > 0)
            throw new NotSupportedException($"Writing <originalData> (unit '{unit.Id}') is not supported yet.");

        return new XElement(Ns + "unit",
            new XAttribute("id", unit.Id),
            OptionalAttribute("name", unit.Name),
            unit.Notes.Count == 0 ? null : new XElement(Ns + "notes", unit.Notes.Select(WriteNote)),
            unit.Segments.Select(WriteSegment));
    }

    private static XElement WriteSegment(Segment segment)
    {
        var element = new XElement(Ns + (segment.IsIgnorable ? "ignorable" : "segment"),
            OptionalAttribute("id", segment.Id));

        if (!segment.IsIgnorable && segment.State != SegmentState.Initial)
            element.Add(new XAttribute("state", FormatState(segment.State)));
        if (!segment.IsIgnorable && segment.SubState is not null)
            element.Add(new XAttribute("subState", segment.SubState));

        element.Add(WriteContent(Ns + "source", segment.Source));
        if (segment.Target is not null)
            element.Add(WriteContent(Ns + "target", segment.Target));

        return element;
    }

    private static XElement WriteNote(Note note) =>
        new(Ns + "note",
            OptionalAttribute("id", note.Id),
            OptionalAttribute("category", note.Category),
            OptionalAttribute("priority", note.Priority?.ToString(CultureInfo.InvariantCulture)),
            OptionalAttribute("appliesTo", note.AppliesTo switch
            {
                NoteAppliesTo.Source => "source",
                NoteAppliesTo.Target => "target",
                _ => null,
            }),
            note.Text);

    private static XElement WriteContent(XName name, InlineContent content)
    {
        // Fail loudly: writing only the text would drop the codes from the saved file.
        if (content.HasTags)
            throw new NotSupportedException("Writing inline codes and markers is not supported yet.");

        var text = content.ToPlainText();
        if (!IsValidXml(text))
            throw new NotSupportedException("Writing characters that are invalid in XML (as <cp>) is not supported yet.");

        var element = new XElement(name, text);

        // Tell other tools not to normalize whitespace that is significant for the translation.
        if (HasSignificantWhitespace(text))
            element.Add(new XAttribute(XmlSpace, "preserve"));

        return element;
    }

    private static bool IsValidXml(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (!XmlConvert.IsXmlChar(text[i]))
                return false;
        }

        return true;
    }

    private static bool HasSignificantWhitespace(string text)
    {
        if (text.Length == 0)
            return false;
        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
            return true;

        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) && (char.IsWhiteSpace(text[i - 1]) || text[i] != ' '))
                return true;
        }

        return false;
    }

    private static string FormatState(SegmentState state) => state switch
    {
        SegmentState.Initial => "initial",
        SegmentState.Translated => "translated",
        SegmentState.Reviewed => "reviewed",
        SegmentState.Final => "final",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static XAttribute? OptionalAttribute(XName name, string? value) =>
        value is null ? null : new XAttribute(name, value);

    // ---------------------------------------------------------------- inline reading helpers

    /// <summary>Id and pairing bookkeeping for the sources or the targets of one unit.</summary>
    /// <remarks>
    /// Targets reuse the ids of the corresponding source codes, so each side has its own id set.
    /// Pairs may span segments, so open pairs are tracked across the whole unit.
    /// </remarks>
    private sealed class InlineSide(
        string name, string unitId, IReadOnlyDictionary<string, string> originalData, IReadOnlySet<string> segmentIds)
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _openCodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _openMarkers = new(StringComparer.Ordinal);

        public string Name => name;

        public string UnitId => unitId;

        public string DefineId(string id)
        {
            if (segmentIds.Contains(id) || !_ids.Add(id))
                throw new XliffFormatException($"Duplicate id '{id}' in the {name} of unit '{unitId}'.");
            return id;
        }

        public void OpenCode(string id) => _openCodes.Add(id);

        public void OpenMarker(string id) => _openMarkers.Add(id);

        public string CloseCode(string startRef) =>
            _openCodes.Remove(startRef)
                ? startRef
                : throw new XliffFormatException(
                    $"<ec startRef=\"{startRef}\"> in the {name} of unit '{unitId}' has no matching <sc> before it.");

        public string CloseMarker(string startRef) =>
            _openMarkers.Remove(startRef)
                ? startRef
                : throw new XliffFormatException(
                    $"<em startRef=\"{startRef}\"> in the {name} of unit '{unitId}' has no matching <sm> before it.");

        public string? DataRef(string? id) =>
            id is null || originalData.ContainsKey(id)
                ? id
                : throw new XliffFormatException($"dataRef '{id}' in unit '{unitId}' does not match any <data> in <originalData>.");

        public void VerifyPairsClosed()
        {
            foreach (var id in _openCodes)
                throw new XliffFormatException(
                    $"<sc id=\"{id}\"> in the {name} of unit '{unitId}' has no matching <ec>. Mark it isolated=\"yes\" if its end is outside the unit.");
            foreach (var id in _openMarkers)
                throw new XliffFormatException($"<sm id=\"{id}\"> in the {name} of unit '{unitId}' has no matching <em>.");
        }
    }

    /// <summary>
    /// Reads attributes and keeps track of which ones were used, so unknown XLIFF attributes fail loudly
    /// and attributes from other namespaces are kept as <see cref="ExtensionAttr"/>s.
    /// </summary>
    private sealed class InlineAttributes(XElement element, string unitId)
    {
        private readonly HashSet<XName> _read = [];

        public string? Optional(XName name)
        {
            _read.Add(name);
            return (string?)element.Attribute(name);
        }

        public string Required(XName name) =>
            Optional(name)
            ?? throw new XliffFormatException(
                $"<{element.Name.LocalName}> in unit '{unitId}' is missing the required '{name.LocalName}' attribute.");

        public bool? YesNo(string name) => Optional(name) switch
        {
            null => null,
            "yes" => true,
            "no" => false,
            var other => throw Invalid(name, other),
        };

        public CanReorder? CanReorder() => Optional("canReorder") switch
        {
            null => null,
            "yes" => Models.CanReorder.Yes,
            "firstNo" => Models.CanReorder.FirstNo,
            "no" => Models.CanReorder.No,
            var other => throw Invalid("canReorder", other),
        };

        /// <summary>Call after reading all supported attributes.</summary>
        public List<ExtensionAttr> ExtensionAttributes()
        {
            var result = new List<ExtensionAttr>();
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || _read.Contains(attribute.Name))
                    continue;

                var ns = attribute.Name.Namespace;
                if (ns == XNamespace.None || ns == Ns || ns == XNamespace.Xml)
                    throw new XliffFormatException(
                        $"Unsupported attribute '{attribute.Name.LocalName}' on <{element.Name.LocalName}> in unit '{unitId}'.");

                result.Add(new ExtensionAttr(ns.NamespaceName, attribute.Name.LocalName, attribute.Value));
            }

            return result;
        }

        /// <summary>For elements that can't carry extension attributes.</summary>
        public void VerifyNoneLeft()
        {
            var extensions = ExtensionAttributes();
            if (extensions.Count > 0)
                throw new XliffFormatException(
                    $"Unsupported attribute '{extensions[0].LocalName}' on <{element.Name.LocalName}> in unit '{unitId}'.");
        }

        private XliffFormatException Invalid(string name, string value) =>
            new($"Invalid {name} value '{value}' on <{element.Name.LocalName}> in unit '{unitId}'.");
    }
}
