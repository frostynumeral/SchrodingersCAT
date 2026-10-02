using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Xliff;

/// <summary>
/// XLIFF 2.0 / 2.1 / 2.2 core serializer.
/// </summary>
/// <remarks>
/// Current limitations (intentional for the first iteration):
/// <list type="bullet">
/// <item>Well-formed pairs are always written as <c>&lt;pc&gt;</c>/<c>&lt;mrk&gt;</c>, even if the file had
/// <c>&lt;sc&gt;/&lt;ec&gt;</c> or <c>&lt;sm&gt;/&lt;em&gt;</c>. XLIFF treats the forms as equivalent.</item>
/// <item><c>&lt;group&gt;</c> hierarchy is flattened, and modules/extensions (matches, glossary, …) are not
/// preserved: the writer regenerates the file from the model.</item>
/// </list>
/// </remarks>
public sealed partial class Xliff20Serializer(ILogger<Xliff20Serializer>? logger = null) : IXliffSerializer
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:document:2.0";

    private readonly ILogger _logger = logger ?? NullLogger<Xliff20Serializer>.Instance;

    private static readonly XName XmlSpace = XNamespace.Xml + "space";

    // Conventional prefixes for namespaces whose attributes appear on inline elements.
    private static readonly Dictionary<string, string> KnownPrefixes = new(StringComparer.Ordinal)
    {
        ["urn:oasis:names:tc:xliff:fs:2.0"] = "fs",
        ["urn:oasis:names:tc:xliff:sizerestriction:2.0"] = "slr",
        ["http://www.w3.org/2005/11/its"] = "its",
    };

    // ---------------------------------------------------------------- logging

    [LoggerMessage(Level = LogLevel.Debug, Message = "Read XLIFF document with {FileCount} file(s) and {UnitCount} unit(s)")]
    private partial void LogRead(int fileCount, int unitCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to read XLIFF document: {Reason}")]
    private partial void LogReadFailed(Exception exception, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote XLIFF document with {FileCount} file(s) and {UnitCount} unit(s)")]
    private partial void LogWritten(int fileCount, int unitCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write XLIFF document: {Reason}")]
    private partial void LogWriteFailed(Exception exception, string reason);

    // ---------------------------------------------------------------- reading

    public async Task<TranslationDocument> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var document = await ReadDocumentAsync(input, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var unitCount = document.Files.Sum(f => f.Units.Count);
                LogRead(document.Files.Count, unitCount);
            }
            return document;
        }
        catch (XliffFormatException ex)
        {
            LogReadFailed(ex, ex.Message);
            throw;
        }
    }

    private static async Task<TranslationDocument> ReadDocumentAsync(Stream input, CancellationToken cancellationToken)
    {
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

        try
        {
            await WriteDocumentAsync(document, output, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var unitCount = document.Files.Sum(f => f.Units.Count);
                LogWritten(document.Files.Count, unitCount);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            LogWriteFailed(ex, ex.Message);
            throw;
        }
    }

    private static async Task WriteDocumentAsync(TranslationDocument document, Stream output, CancellationToken cancellationToken)
    {
        var root = new XElement(Ns + "xliff",
            new XAttribute("version", "2.0"),
            new XAttribute("srcLang", document.SourceLanguage),
            OptionalAttribute("trgLang", document.TargetLanguage),
            document.Files.Select(WriteFile));
        DeclareExtensionNamespaces(root);

        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            // Safe for our content: XmlWriter stops indenting inside elements that contain text,
            // and WriteContent makes sure every content element does.
            Indent = true,
        };

        await using var writer = XmlWriter.Create(output, settings);
        await xml.SaveAsync(writer, cancellationToken);
    }

    // Declared once on the root; otherwise XLinq repeats a generated xmlns:p1 on every element that needs it.
    private static void DeclareExtensionNamespaces(XElement root)
    {
        var namespaces = root.Descendants().Attributes()
            .Select(a => a.Name.Namespace)
            .Where(ns => ns != XNamespace.None && ns != XNamespace.Xml)
            .Distinct()
            .ToList();

        var generated = 0;
        foreach (var ns in namespaces)
        {
            var prefix = KnownPrefixes.GetValueOrDefault(ns.NamespaceName) ?? $"ns{++generated}";
            root.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }
    }

    private static XElement WriteFile(TranslationFile file) =>
        new(Ns + "file",
            new XAttribute("id", file.Id),
            OptionalAttribute("original", file.Original),
            file.Units.Select(WriteUnit));

    private static XElement WriteUnit(TranslationUnit unit)
    {
        VerifyInlineStructure(unit);

        return new XElement(Ns + "unit",
            new XAttribute("id", unit.Id),
            OptionalAttribute("name", unit.Name),
            unit.Notes.Count == 0 ? null : new XElement(Ns + "notes", unit.Notes.Select(WriteNote)),
            unit.OriginalData.Count == 0 ? null : new XElement(Ns + "originalData", unit.OriginalData.Select(WriteData)),
            unit.Segments.Select(WriteSegment));
    }

    private static XElement WriteData(KeyValuePair<string, string> data)
    {
        // See WriteContent for the leading empty text.
        var element = new XElement(Ns + "data", new XAttribute("id", data.Key), new XText(string.Empty));
        AddText(element, data.Value);
        if (HasSignificantWhitespace(data.Value))
            element.Add(new XAttribute(XmlSpace, "preserve"));
        return element;
    }

    // The reader rejects duplicate ids, unpaired halves and unknown dataRefs, so the writer must not produce them.
    private static void VerifyInlineStructure(TranslationUnit unit)
    {
        var segmentIds = unit.Segments.Select(s => s.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        VerifyInlineStructure(unit, "source", unit.Segments.Select(s => s.Source), segmentIds);
        VerifyInlineStructure(unit, "target", unit.Segments.Select(s => s.Target).OfType<InlineContent>(), segmentIds);
    }

    private static void VerifyInlineStructure(
        TranslationUnit unit, string side, IEnumerable<InlineContent> contents, HashSet<string> segmentIds)
    {
        var ids = new HashSet<string>(segmentIds, StringComparer.Ordinal);
        var openCodes = new HashSet<string>(StringComparer.Ordinal);
        var openMarkers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in contents.SelectMany(c => c))
        {
            switch (element)
            {
                case InlineCode { Kind: InlineCodeKind.Closing, IsIsolated: false } code:
                    if (!openCodes.Remove(code.Id))
                        throw Invalid($"the closing half of code '{code.Id}' has no opening half before it. Set IsIsolated if the other half is outside the unit.");
                    break;
                case InlineCode code:
                    DefineId(code.Id);
                    if (code is { Kind: InlineCodeKind.Opening, IsIsolated: false })
                        openCodes.Add(code.Id);
                    break;
                case InlineMarker { Kind: InlineMarkerKind.Closing } marker:
                    if (!openMarkers.Remove(marker.Id))
                        throw Invalid($"the closing half of marker '{marker.Id}' has no opening half before it.");
                    break;
                case InlineMarker marker:
                    DefineId(marker.Id);
                    openMarkers.Add(marker.Id);
                    break;
            }

            if (element is InlineCode { DataRef: { } dataRef } && !unit.OriginalData.ContainsKey(dataRef))
                throw Invalid($"dataRef '{dataRef}' does not match any key in OriginalData.");
        }

        if (openCodes.FirstOrDefault() is { } openCode)
            throw Invalid($"the opening half of code '{openCode}' has no closing half. Set IsIsolated if the other half is outside the unit.");
        if (openMarkers.FirstOrDefault() is { } openMarker)
            throw Invalid($"the opening half of marker '{openMarker}' has no closing half.");

        void DefineId(string id)
        {
            if (!ids.Add(id))
                throw Invalid($"the id '{id}' is used more than once (inline ids share one id space with segment ids).");
        }

        InvalidOperationException Invalid(string message) =>
            new($"Cannot write unit '{unit.Id}': in its {side}, {message}");
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
        // The leading empty text makes this mixed content, so XmlWriter does not indent inside it. Indenting
        // content that starts with an element would add whitespace to the translatable text.
        var element = new XElement(name, new XText(string.Empty));
        var pairs = FindWellFormedPairs(content);
        var parents = new Stack<XElement>();
        var current = element;

        foreach (var item in content)
        {
            switch (item)
            {
                case InlineText text:
                    AddText(current, text.Text);
                    break;

                case InlineCode code when pairs.TryGetValue(code.Id, out var closing):
                    if (code.Kind == InlineCodeKind.Opening)
                        Open(WritePairedCode(code, (InlineCode)closing));
                    else
                        current = parents.Pop();
                    break;

                case InlineMarker marker when pairs.ContainsKey(marker.Id):
                    if (marker.Kind == InlineMarkerKind.Opening)
                    {
                        Open(new XElement(Ns + "mrk", MarkerAttributes(marker)));
                    }
                    else
                    {
                        VerifyNoAttributes(marker);
                        current = parents.Pop();
                    }

                    break;

                case InlineCode code:
                    current.Add(WriteCode(code));
                    break;

                case InlineMarker marker:
                    current.Add(WriteMarker(marker));
                    break;
            }
        }

        // Tell other tools not to normalize whitespace that is significant for the translation.
        // Codes count as characters here, so text next to a code doesn't look like leading/trailing whitespace.
        var probe = string.Concat(content.Select(e => e is InlineText t ? t.Text : "￼"));
        if (HasSignificantWhitespace(probe))
            element.Add(new XAttribute(XmlSpace, "preserve"));

        return element;

        void Open(XElement child)
        {
            current.Add(child);
            parents.Push(current);
            current = child;
        }
    }

    /// <summary>
    /// Finds the pairs to write as <c>&lt;pc&gt;</c>/<c>&lt;mrk&gt;</c>, keyed by id, with their closing half.
    /// </summary>
    /// <remarks>
    /// A pair qualifies if both halves are in this content, it nests properly with the other qualifying pairs,
    /// and (for codes) <c>&lt;pc&gt;</c> can express both halves' attributes. All other halves are written as
    /// <c>&lt;sc&gt;</c>/<c>&lt;ec&gt;</c> or <c>&lt;sm&gt;</c>/<c>&lt;em&gt;</c>, which XLIFF treats as equivalent.
    /// </remarks>
    private static Dictionary<string, InlineElement> FindWellFormedPairs(InlineContent content)
    {
        var closings = new Dictionary<string, InlineElement>(StringComparer.Ordinal);
        foreach (var element in content)
        {
            if (element is InlineCode { Kind: InlineCodeKind.Closing, IsIsolated: false } code)
                closings.Add(code.Id, code);
            else if (element is InlineMarker { Kind: InlineMarkerKind.Closing } marker)
                closings.Add(marker.Id, marker);
        }

        var pairs = new Dictionary<string, InlineElement>(StringComparer.Ordinal);
        var open = new List<string>();
        foreach (var element in content)
        {
            switch (element)
            {
                case InlineCode { Kind: InlineCodeKind.Opening, IsIsolated: false } code
                    when closings.TryGetValue(code.Id, out var closing) && CanWriteAsPc(code, (InlineCode)closing):
                    open.Add(code.Id);
                    break;

                case InlineMarker { Kind: InlineMarkerKind.Opening } marker when closings.ContainsKey(marker.Id):
                    open.Add(marker.Id);
                    break;

                case InlineCode { Kind: InlineCodeKind.Closing } or InlineMarker { Kind: InlineMarkerKind.Closing }:
                    var id = element is InlineCode c ? c.Id : ((InlineMarker)element).Id;
                    if (open.Count > 0 && open[^1] == id)
                        pairs.Add(id, closings[id]);

                    // If it wasn't innermost, it overlaps a pair opened after it and falls back to <sc>/<ec>.
                    open.Remove(id);
                    break;
            }
        }

        return pairs;
    }

    // <pc> has one set of pair-level attributes, and its dir belongs to the opening half (see the reader).
    private static bool CanWriteAsPc(InlineCode opening, InlineCode closing) =>
        closing.Type == opening.Type
        && closing.SubType == opening.SubType
        && closing.CopyOf == opening.CopyOf
        && closing.CanCopy == opening.CanCopy
        && closing.CanDelete == opening.CanDelete
        && closing.CanOverlap == opening.CanOverlap
        && closing.CanReorder == (opening.CanReorder == CanReorder.FirstNo ? CanReorder.No : opening.CanReorder)
        && closing.Dir is null
        && closing.ExtensionAttributes.Count == 0;

    private static XElement WritePairedCode(InlineCode opening, InlineCode closing) =>
        new(Ns + "pc",
            new XAttribute("id", opening.Id),
            PairLevelAttributes(opening, canOverlapDefault: false),
            OptionalAttribute("dir", opening.Dir),
            OptionalAttribute("dataRefStart", opening.DataRef),
            OptionalAttribute("dataRefEnd", closing.DataRef),
            OptionalAttribute("dispStart", opening.Disp),
            OptionalAttribute("dispEnd", closing.Disp),
            OptionalAttribute("equivStart", opening.Equiv),
            OptionalAttribute("equivEnd", closing.Equiv),
            OptionalAttribute("subFlowsStart", opening.SubFlows),
            OptionalAttribute("subFlowsEnd", closing.SubFlows),
            WriteExtensionAttributes(opening.ExtensionAttributes));

    private static XElement WriteCode(InlineCode code)
    {
        switch (code.Kind)
        {
            case InlineCodeKind.Standalone:
                if (code.Dir is not null || code.CanOverlap is not null || code.IsIsolated)
                    throw new NotSupportedException(
                        $"Standalone code '{code.Id}' has Dir, CanOverlap or IsIsolated set, which <ph> can't carry.");
                return new XElement(Ns + "ph",
                    new XAttribute("id", code.Id),
                    PairLevelAttributes(code, canOverlapDefault: false),
                    CodeAttributes(code));

            case InlineCodeKind.Opening:
                return new XElement(Ns + "sc",
                    new XAttribute("id", code.Id),
                    code.IsIsolated ? new XAttribute("isolated", "yes") : null,
                    PairLevelAttributes(code, canOverlapDefault: true),
                    OptionalAttribute("dir", code.Dir),
                    CodeAttributes(code));

            default:
                // A non-isolated <ec> refers to its <sc> instead of having an id, and may not carry dir.
                if (!code.IsIsolated && code.Dir is not null)
                    throw new NotSupportedException(
                        $"Closing code '{code.Id}' has Dir set, which <ec> only allows when it is isolated.");
                return new XElement(Ns + "ec",
                    new XAttribute(code.IsIsolated ? "id" : "startRef", code.Id),
                    code.IsIsolated ? new XAttribute("isolated", "yes") : null,
                    PairLevelAttributes(code, canOverlapDefault: true),
                    OptionalAttribute("dir", code.Dir),
                    CodeAttributes(code));
        }
    }

    private static IEnumerable<XAttribute?> PairLevelAttributes(InlineCode code, bool canOverlapDefault) =>
    [
        OptionalAttribute("type", code.Type),
        OptionalAttribute("subType", code.SubType),
        OptionalAttribute("copyOf", code.CopyOf),
        code.CanCopy ? null : new XAttribute("canCopy", "no"),
        code.CanDelete ? null : new XAttribute("canDelete", "no"),
        OptionalAttribute("canReorder", code.CanReorder switch
        {
            CanReorder.Yes => null,
            CanReorder.FirstNo => "firstNo",
            CanReorder.No => "no",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code.CanReorder, null),
        }),
        // null means unspecified: write nothing and let the element's default apply.
        code.CanOverlap is { } canOverlap && canOverlap != canOverlapDefault
            ? new XAttribute("canOverlap", canOverlap ? "yes" : "no")
            : null,
    ];

    // The attributes <ph>, <sc> and <ec> share; <pc> has start/end variants instead.
    private static IEnumerable<XAttribute?> CodeAttributes(InlineCode code) =>
    [
        OptionalAttribute("dataRef", code.DataRef),
        OptionalAttribute("disp", code.Disp),
        OptionalAttribute("equiv", code.Equiv),
        OptionalAttribute("subFlows", code.SubFlows),
        .. WriteExtensionAttributes(code.ExtensionAttributes),
    ];

    private static XElement WriteMarker(InlineMarker marker)
    {
        if (marker.Kind == InlineMarkerKind.Opening)
            return new XElement(Ns + "sm", MarkerAttributes(marker));

        VerifyNoAttributes(marker);
        return new XElement(Ns + "em", new XAttribute("startRef", marker.Id));
    }

    private static IEnumerable<XAttribute?> MarkerAttributes(InlineMarker marker) =>
    [
        new XAttribute("id", marker.Id),
        marker.Translate is { } translate ? new XAttribute("translate", translate ? "yes" : "no") : null,
        OptionalAttribute("type", marker.Type),
        OptionalAttribute("ref", marker.Ref),
        OptionalAttribute("value", marker.Value),
        .. WriteExtensionAttributes(marker.ExtensionAttributes),
    ];

    // </mrk> and <em/> carry no attributes, so there is nowhere to write a closing marker's.
    private static void VerifyNoAttributes(InlineMarker closing)
    {
        if (closing != new InlineMarker(closing.Id, closing.Kind))
            throw new NotSupportedException(
                $"The closing half of marker '{closing.Id}' has attributes set; XLIFF only allows them on the opening half.");
    }

    private static IEnumerable<XAttribute> WriteExtensionAttributes(IEnumerable<ExtensionAttr> attributes) =>
        attributes.Select(a =>
        {
            XNamespace ns = a.Namespace;
            if (ns == XNamespace.None || ns == Ns || ns == XNamespace.Xml || ns == XNamespace.Xmlns)
                throw new NotSupportedException(
                    $"Extension attribute '{a.LocalName}' must be in a namespace other than XLIFF's or XML's own.");
            return new XAttribute(ns + a.LocalName, a.Value);
        });

    /// <summary>Adds text, writing characters that are invalid in XML as <c>&lt;cp&gt;</c>.</summary>
    private static void AddText(XElement parent, string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (XmlConvert.IsXmlChar(text[i]))
                continue;

            if (i > start)
                parent.Add(text[start..i]);
            parent.Add(new XElement(Ns + "cp",
                new XAttribute("hex", ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture))));
            start = i + 1;
        }

        if (start < text.Length)
            parent.Add(text[start..]);
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
