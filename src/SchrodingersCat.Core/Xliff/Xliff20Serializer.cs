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
/// The writer regenerates the file from the model, so the reader keeps everything or rejects it:
/// <list type="bullet">
/// <item><c>&lt;group&gt;</c> nesting is kept as <see cref="TranslationGroup"/>s.</item>
/// <item>Module and extension elements (matches, glossary, metadata, …) and <c>&lt;skeleton&gt;</c> are kept
/// verbatim as <see cref="XElement"/>s; attributes from other namespaces are kept as <see cref="ExtensionAttr"/>s.
/// The serializer doesn't interpret either, so it doesn't update them when the content changes.</item>
/// <item>Well-formed pairs are always written as <c>&lt;pc&gt;</c>/<c>&lt;mrk&gt;</c>, even if the file had
/// <c>&lt;sc&gt;/&lt;ec&gt;</c> or <c>&lt;sm&gt;/&lt;em&gt;</c>. XLIFF treats the forms as equivalent.</item>
/// <item>Not kept, because they carry no data: namespace prefixes, comments and processing instructions outside
/// content, <c>xml:space</c> outside content (whitespace is always read verbatim), and <c>xml:lang</c> on
/// <c>&lt;source&gt;</c>/<c>&lt;target&gt;</c>, which must repeat <c>srcLang</c>/<c>trgLang</c>.</item>
/// </list>
/// </remarks>
public sealed partial class Xliff20Serializer(ILogger<Xliff20Serializer>? logger = null) : IXliffSerializer
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:document:2.0";

    private readonly ILogger _logger = logger ?? NullLogger<Xliff20Serializer>.Instance;

    /// <summary>Files nested deeper than this are rejected while parsing, before the (recursive) reading starts.</summary>
    /// <remarks>
    /// Real XLIFF stays well below it. A malicious file nested thousands deep would otherwise overflow the stack and
    /// crash the process, and building a very deep XML tree is quadratically slow.
    /// </remarks>
    public const int MaxElementDepth = 256;

    /// <summary>
    /// The largest input <see cref="ReadAsync"/> accepts, in bytes (default 256 MiB). Larger input throws
    /// <see cref="XliffFormatException"/> as soon as the limit is passed, so an endless stream can't exhaust memory.
    /// </summary>
    public long MaxInputBytes { get; init; } = 256L * 1024 * 1024;

    private static readonly XName XmlSpace = XNamespace.Xml + "space";

    // Conventional prefixes for the XLIFF modules and ITS. Other namespaces get generated ns1, ns2, … prefixes.
    private static readonly Dictionary<string, string> KnownPrefixes = new(StringComparer.Ordinal)
    {
        ["urn:oasis:names:tc:xliff:matches:2.0"] = "mtc",
        ["urn:oasis:names:tc:xliff:glossary:2.0"] = "gls",
        ["urn:oasis:names:tc:xliff:fs:2.0"] = "fs",
        ["urn:oasis:names:tc:xliff:metadata:2.0"] = "mda",
        ["urn:oasis:names:tc:xliff:resourcedata:2.0"] = "res",
        ["urn:oasis:names:tc:xliff:changetracking:2.0"] = "ctr",
        ["urn:oasis:names:tc:xliff:sizerestriction:2.0"] = "slr",
        ["urn:oasis:names:tc:xliff:validation:2.0"] = "val",
        ["urn:oasis:names:tc:xliff:itsm:2.1"] = "itsm",
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
            var document = await ReadDocumentAsync(input, MaxInputBytes, cancellationToken);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var unitCount = document.Files.Sum(f => f.Units.Count());
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

    // XLIFF files come from clients and vendors, so they are untrusted input. XLIFF never uses a DTD, and a DTD is
    // what makes entity expansion ("billion laughs") and external entities possible, so DTDs are refused outright.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false,
    };

    private static async Task<TranslationDocument> ReadDocumentAsync(Stream input, long maxInputBytes, CancellationToken cancellationToken)
    {
        XDocument xml;
        try
        {
            using var reader = new DepthLimitingXmlReader(
                XmlReader.Create(new LimitedReadStream(input, maxInputBytes), ReaderSettings), MaxElementDepth);

            // Whitespace inside <source>/<target> is part of the translatable content.
            xml = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.Ordinal))
        {
            throw new XliffFormatException(
                "The file contains a DTD (<!DOCTYPE>). XLIFF doesn't use DTDs, and they are refused because they can expand entities or reference external files.", ex);
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

        var a = new AttributeReader(root, location: null);
        a.Optional("version");
        var document = new TranslationDocument
        {
            SourceLanguage = a.Required("srcLang"),
            TargetLanguage = a.Optional("trgLang"),
        };
        ReadInheritedSpace(a);
        document.ExtensionAttributes.AddRange(a.ExtensionAttributes());

        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        ReadChildren(root, "<xliff>", child =>
        {
            if (child.Name != Ns + "file")
                return false;

            var file = ReadFile(child, document);
            if (!fileIds.Add(file.Id))
                throw new XliffFormatException($"Duplicate file id '{file.Id}'.");
            document.Files.Add(file);
            return true;
        });

        if (document.Files.Count == 0)
            throw new XliffFormatException("The document contains no <file>.");

        return document;
    }

    private static TranslationFile ReadFile(XElement fileElement, TranslationDocument document)
    {
        var a = new AttributeReader(fileElement, location: null);
        var file = new TranslationFile
        {
            Id = a.Required("id"),
            Original = a.Optional("original"),
            CanResegment = a.YesNo("canResegment"),
            Translate = a.YesNo("translate"),
            SourceDir = a.Direction("srcDir"),
            TargetDir = a.Direction("trgDir"),
        };
        ReadInheritedSpace(a);
        file.ExtensionAttributes.AddRange(a.ExtensionAttributes());

        var context = new FileContext(document, file.Id);
        ReadChildren(fileElement, context.Location, child =>
        {
            if (child.Name == Ns + "skeleton")
            {
                if (file.Skeleton is not null)
                    throw new XliffFormatException($"File '{file.Id}' has more than one <skeleton>.");
                file.Skeleton = ReadSkeleton(child, context.Location);
            }
            else if (child.Name == Ns + "notes")
            {
                ReadNotes(child, file.Notes, context.Location);
            }
            else
            {
                return ReadItem(child, file.Items, context);
            }

            return true;
        }, file.ExtensionElements);

        if (file.Items.Count == 0)
            throw new XliffFormatException($"File '{file.Id}' contains no <unit> or <group>.");

        return file;
    }

    // XLIFF reads text verbatim anyway, and the writer marks significant whitespace where it occurs.
    private static void ReadInheritedSpace(AttributeReader a) => a.Optional(XmlSpace);

    /// <summary>
    /// Reads the children of a structural element. Elements from other namespaces are modules or extensions: they
    /// are kept in <paramref name="extensionElements"/>, or rejected where XLIFF allows none (null). XLIFF elements
    /// <paramref name="readCore"/> doesn't accept, and text other than whitespace, are rejected.
    /// </summary>
    private static void ReadChildren(
        XElement parent, string owner, Func<XElement, bool> readCore, List<XElement>? extensionElements = null)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                throw new XliffFormatException($"Text is not allowed directly in {owner}.");
            if (node is not XElement child || readCore(child))
                continue;

            var ns = child.Name.Namespace;
            if (extensionElements is null || ns == Ns || ns == XNamespace.None)
                throw new XliffFormatException($"<{child.Name.LocalName}> is not allowed in {owner}.");

            extensionElements.Add(Detach(child));
        }
    }

    /// <summary>
    /// Copies an element the serializer keeps verbatim. The copy declares the prefixes it uses, which were declared
    /// on its ancestors, so the writer can declare the same ones again.
    /// </summary>
    private static XElement Detach(XElement element)
    {
        var copy = new XElement(element);
        var namespaces = element.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Prepend(e.Name.Namespace))
            .Where(ns => ns != XNamespace.None && ns != XNamespace.Xml && ns != Ns)
            .Distinct();

        foreach (var ns in namespaces)
        {
            if (copy.GetPrefixOfNamespace(ns) is null && element.GetPrefixOfNamespace(ns) is { } prefix
                && copy.Attribute(XNamespace.Xmlns + prefix) is null)
                copy.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }

        return copy;
    }

    private static bool ReadItem(XElement element, List<TranslationItem> items, FileContext context)
    {
        if (element.Name == Ns + "unit")
            items.Add(ReadUnit(element, context));
        else if (element.Name == Ns + "group")
            items.Add(ReadGroup(element, context));
        else
            return false;

        return true;
    }

    private static TranslationGroup ReadGroup(XElement groupElement, FileContext context)
    {
        var a = new AttributeReader(groupElement, context.Location);
        var group = new TranslationGroup { Id = a.Required("id") };
        context.DefineGroup(group.Id);
        ReadItemAttributes(group, a);

        var owner = $"group '{group.Id}'";
        ReadChildren(groupElement, owner, child =>
        {
            if (child.Name != Ns + "notes")
                return ReadItem(child, group.Items, context);

            ReadNotes(child, group.Notes, owner);
            return true;
        }, group.ExtensionElements);

        return group;
    }

    private static void ReadItemAttributes(TranslationItem item, AttributeReader a)
    {
        item.Name = a.Optional("name");
        item.Type = a.Optional("type");
        item.CanResegment = a.YesNo("canResegment");
        item.Translate = a.YesNo("translate");
        item.SourceDir = a.Direction("srcDir");
        item.TargetDir = a.Direction("trgDir");
        ReadInheritedSpace(a);
        item.ExtensionAttributes.AddRange(a.ExtensionAttributes());
    }

    private static TranslationUnit ReadUnit(XElement unitElement, FileContext context)
    {
        var a = new AttributeReader(unitElement, context.Location);
        var unit = new TranslationUnit { Id = a.Required("id") };
        context.DefineUnit(unit.Id);
        ReadItemAttributes(unit, a);

        var owner = $"unit '{unit.Id}'";

        // Segments refer to <originalData> and to each other's ids, so read those first.
        var originalDataElements = unitElement.Elements(Ns + "originalData").ToList();
        if (originalDataElements.Count > 1)
            throw new XliffFormatException($"Unit '{unit.Id}' has more than one <originalData>.");
        if (originalDataElements.Count == 1)
            ReadOriginalData(originalDataElements[0], unit);

        var segmentIds = ReadSegmentIds(unitElement, unit.Id);
        var source = new InlineSide("source", unit.Id, unit.OriginalData, segmentIds);
        var target = new InlineSide("target", unit.Id, unit.OriginalData, segmentIds);

        // Segments and ignorables are siblings and their order matters (it rebuilds the full text).
        ReadChildren(unitElement, owner, child =>
        {
            if (child.Name == Ns + "segment")
                unit.Segments.Add(ReadSegment(child, source, target, context.Document, isIgnorable: false));
            else if (child.Name == Ns + "ignorable")
                unit.Segments.Add(ReadSegment(child, source, target, context.Document, isIgnorable: true));
            else if (child.Name == Ns + "notes")
                ReadNotes(child, unit.Notes, owner);
            else if (child.Name != Ns + "originalData") // already read
                return false;

            return true;
        }, unit.ExtensionElements);

        source.VerifyPairsClosed();
        target.VerifyPairsClosed();

        if (FindTargetOrderProblem(unit) is { } orderProblem)
            throw new XliffFormatException($"Unit '{unit.Id}': {orderProblem}.");

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

    private static XElement ReadSkeleton(XElement element, string owner)
    {
        var a = new AttributeReader(element, owner);
        var href = a.Optional("href");
        a.VerifyNoneLeft();

        // The content belongs to the extracting tool: text and elements from other namespaces.
        if (element.Elements().FirstOrDefault(e => e.Name.Namespace == Ns || e.Name.Namespace == XNamespace.None) is { } core)
            throw new XliffFormatException($"<{core.Name.LocalName}> is not allowed in the <skeleton> of {owner}.");
        if (href is null && !element.Nodes().Any(n => n is not XComment))
            throw new XliffFormatException($"The <skeleton> of {owner} has neither content nor an href.");

        return Detach(element);
    }

    private static void ReadNotes(XElement element, List<Note> notes, string owner)
    {
        if (notes.Count > 0)
            throw new XliffFormatException($"{Capitalize(owner)} has more than one <notes>.");

        new AttributeReader(element, owner).VerifyNoneLeft();
        ReadChildren(element, $"<notes> in {owner}", child =>
        {
            if (child.Name != Ns + "note")
                return false;

            notes.Add(ReadNote(child, owner));
            return true;
        });

        if (notes.Count == 0)
            throw new XliffFormatException($"<notes> in {owner} contains no <note>.");
        if (FindDuplicateNoteId(notes) is { } duplicate)
            throw new XliffFormatException($"Duplicate note id '{duplicate}' in {owner}.");
    }

    private static void ReadOriginalData(XElement element, TranslationUnit unit)
    {
        var location = $"unit '{unit.Id}'";
        new AttributeReader(element, location).VerifyNoneLeft();

        foreach (var dataElement in element.Elements())
        {
            if (dataElement.Name != Ns + "data")
                throw new XliffFormatException($"Unit '{unit.Id}' contains <{dataElement.Name.LocalName}> inside <originalData>, which is not allowed.");

            var attributes = new AttributeReader(dataElement, location);
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

        if (unit.OriginalData.Count == 0)
            throw new XliffFormatException($"<originalData> in unit '{unit.Id}' contains no <data>.");
    }

    private static Segment ReadSegment(
        XElement element, InlineSide source, InlineSide target, TranslationDocument document, bool isIgnorable)
    {
        var unitId = source.UnitId;
        var location = $"unit '{unitId}'";
        var a = new AttributeReader(element, location);
        var segment = new Segment
        {
            Id = a.Optional("id"),
            Source = InlineContent.Empty, // read below, once the attributes are checked
            IsIgnorable = isIgnorable,
        };
        if (!isIgnorable)
        {
            segment.CanResegment = a.YesNo("canResegment");
            segment.State = ParseState(a.Optional("state"), unitId);
            segment.SubState = a.Optional("subState");
        }

        a.VerifyNoneLeft();

        XElement? sourceElement = null;
        XElement? targetElement = null;
        ReadChildren(element, $"a <{element.Name.LocalName}> in {location}", child =>
        {
            if (child.Name == Ns + "source" && sourceElement is null)
                sourceElement = child;
            else if (child.Name == Ns + "target" && sourceElement is not null && targetElement is null)
                targetElement = child;
            else
                return false;

            return true;
        });

        if (sourceElement is null)
            throw new XliffFormatException($"A segment in unit '{unitId}' has no <source>.");

        var sourceAttributes = new AttributeReader(sourceElement, location);
        ReadContentLanguage(sourceAttributes, document.SourceLanguage, "srcLang");
        sourceAttributes.VerifyNoneLeft();
        segment.Source = ReadContent(sourceElement, source);

        if (targetElement is not null)
        {
            if (document.TargetLanguage is null)
                throw new XliffFormatException($"A segment in unit '{unitId}' has a <target>, but <xliff> has no trgLang.");

            var targetAttributes = new AttributeReader(targetElement, location);
            ReadContentLanguage(targetAttributes, document.TargetLanguage, "trgLang");
            segment.TargetOrder = targetAttributes.PositiveInteger("order");
            targetAttributes.VerifyNoneLeft();
            segment.Target = ReadContent(targetElement, target);
        }

        return segment;
    }

    // xml:lang on <source>/<target> must repeat srcLang/trgLang, so the writer can leave it out without losing anything.
    private static void ReadContentLanguage(AttributeReader a, string? documentLanguage, string documentAttribute)
    {
        ReadInheritedSpace(a);
        var language = a.Optional(XNamespace.Xml + "lang");
        if (language is not null && !string.Equals(language, documentLanguage, StringComparison.OrdinalIgnoreCase))
            throw a.Invalid("xml:lang", language, $"it must match the {documentAttribute} of <xliff>");
    }

    private static Note ReadNote(XElement element, string owner)
    {
        if (element.Elements().FirstOrDefault() is { } child)
            throw new XliffFormatException($"<note> in {owner} contains <{child.Name.LocalName}>; notes may only contain text.");

        var a = new AttributeReader(element, owner);
        var priorityText = a.Optional("priority");
        int? priority = null;
        if (priorityText is not null)
        {
            if (!int.TryParse(priorityText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 10)
                throw new XliffFormatException($"Invalid note priority '{priorityText}' (must be 1-10).");
            priority = p;
        }

        var note = new Note
        {
            Id = a.Optional("id"),
            Text = element.Value,
            Category = a.Optional("category"),
            Priority = priority,
            AppliesTo = a.Optional("appliesTo") switch
            {
                null => null,
                "source" => NoteAppliesTo.Source,
                "target" => NoteAppliesTo.Target,
                var other => throw new XliffFormatException($"Invalid note appliesTo value '{other}'."),
            },
        };
        note.ExtensionAttributes.AddRange(a.ExtensionAttributes());
        return note;
    }

    // ---------------------------------------------------------------- constraints shared by reader and writer

    /// <summary>
    /// Target <c>order</c> values must be unique within the unit and point at one of its segments or ignorables
    /// (a target without one stays at its source's position). Returns what is wrong, or null.
    /// </summary>
    private static string? FindTargetOrderProblem(TranslationUnit unit)
    {
        var seen = new HashSet<int>();
        foreach (var order in unit.Segments.Select(s => s.TargetOrder).OfType<int>())
        {
            if (order > unit.Segments.Count)
                return $"target order {order} is past the unit's {unit.Segments.Count} segment(s) and ignorable(s)";
            if (!seen.Add(order))
                return $"target order {order} is used more than once";
        }

        return null;
    }

    /// <summary>Note ids must be unique within the file, group or unit that holds the notes.</summary>
    private static string? FindDuplicateNoteId(IEnumerable<Note> notes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return notes.Select(n => n.Id).OfType<string>().FirstOrDefault(id => !ids.Add(id));
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Reads inline content that stands on its own, outside a unit's segments (a match's <c>&lt;source&gt;</c> or
    /// <c>&lt;target&gt;</c> in the matches module), with the same rules as segment content: its pairs must close
    /// within it, and its <c>dataRef</c>s must be in <paramref name="originalData"/>.
    /// </summary>
    /// <exception cref="XliffFormatException">The content breaks the rules.</exception>
    internal static InlineContent ReadStandaloneContent(XElement element, IReadOnlyDictionary<string, string> originalData, string context)
    {
        var side = new InlineSide(element.Name.LocalName, context, originalData, new HashSet<string>(StringComparer.Ordinal));
        var content = ReadContent(element, side);
        side.VerifyPairsClosed();
        return content;
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

        var a = new AttributeReader(element, $"unit '{side.UnitId}'");
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

    private static InlineCode ReadPairLevel(InlineCode code, AttributeReader a) => code with
    {
        Type = a.Optional("type"),
        SubType = a.Optional("subType"),
        CopyOf = a.Optional("copyOf"),
        CanCopy = a.YesNo("canCopy") ?? true,
        CanDelete = a.YesNo("canDelete") ?? true,
        CanReorder = a.CanReorder() ?? CanReorder.Yes,
    };

    private static InlineMarker ReadMarker(InlineMarker marker, AttributeReader a) => marker with
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
        var attributes = new AttributeReader(element, $"unit '{unitId}'");
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
                var unitCount = document.Files.Sum(f => f.Units.Count());
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
        VerifyDocument(document);

        var root = new XElement(Ns + "xliff",
            new XAttribute("version", "2.0"),
            new XAttribute("srcLang", document.SourceLanguage),
            OptionalAttribute("trgLang", document.TargetLanguage),
            WriteExtensionAttributes(document.ExtensionAttributes),
            document.Files.Select(WriteFile));
        DeclareExtensionNamespaces(root);
        if (document.TargetLanguage is null && document.Units.Any(u => u.Segments.Any(s => s.Target is not null)))
            throw CannotWrite("the document", "it has targets but no TargetLanguage; XLIFF requires trgLang when there are targets");
        VerifyRepresentable(root);

        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            // Safe for our content: XmlWriter stops indenting inside elements that contain text,
            // and WriteContent makes sure every content element does.
            Indent = true,
            // XML parsers turn a literal carriage return into a line feed, so it has to be written as &#xD;.
            NewLineHandling = NewLineHandling.Entitize,
        };

        await using var writer = XmlWriter.Create(output, settings);
        await xml.SaveAsync(writer, cancellationToken);
    }

    // Declared once on the root; otherwise XLinq repeats a generated xmlns:p1 on every element that needs it.
    // Prefer the prefixes verbatim elements brought from the original file (see Detach), then the conventional ones.
    private static void DeclareExtensionNamespaces(XElement root)
    {
        var namespaces = root.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Prepend(e.Name.Namespace))
            .Where(ns => ns != XNamespace.None && ns != XNamespace.Xml && ns != Ns)
            .Distinct()
            .ToList();

        var declarations = root.Descendants().Attributes()
            .Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns)
            .ToList();
        var original = declarations
            .GroupBy(a => a.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Name.LocalName).ToList(), StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var generated = 0;
        foreach (var ns in namespaces)
        {
            var candidates = original.GetValueOrDefault(ns.NamespaceName) ?? [];
            var prefix = candidates.Append(KnownPrefixes.GetValueOrDefault(ns.NamespaceName)).FirstOrDefault(p => p is not null && !used.Contains(p));
            while (prefix is null || used.Contains(prefix))
                prefix = $"ns{++generated}";

            used.Add(prefix);
            root.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }

        // Declarations that now only repeat the root's are noise.
        foreach (var declaration in declarations)
        {
            if ((string?)root.Attribute(declaration.Name) == declaration.Value)
                declaration.Remove();
        }
    }

    private static void VerifyDocument(TranslationDocument document)
    {
        const string owner = "the document";
        if (document.SourceLanguage is null)
            throw CannotWrite(owner, "SourceLanguage is null");
        if (document.Files.Count == 0)
            throw CannotWrite(owner, "it has no files; XLIFF requires at least one <file>");
        VerifyNoNulls(document.Files, owner, "Files");
        VerifyNoNulls(document.ExtensionAttributes, owner, "ExtensionAttributes");

        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in document.Files)
        {
            if (file.Id is null)
                throw CannotWrite("a file", "its Id is null");
            if (!fileIds.Add(file.Id))
                throw CannotWrite(owner, $"the file id '{file.Id}' is used more than once");
        }
    }

    private static XElement WriteFile(TranslationFile file)
    {
        var owner = $"file '{file.Id}'";
        if (file.Items.Count == 0)
            throw CannotWrite(owner, "it has no units or groups; XLIFF requires at least one");
        VerifyNoNulls(file.Notes, owner, "Notes");
        VerifyNoNulls(file.ExtensionElements, owner, "ExtensionElements");
        VerifyNoNulls(file.ExtensionAttributes, owner, "ExtensionAttributes");
        VerifyUniqueItemIds(file);
        VerifySkeleton(file.Skeleton, owner);

        return new XElement(Ns + "file",
            new XAttribute("id", file.Id),
            OptionalAttribute("original", file.Original),
            YesNoAttribute("canResegment", file.CanResegment),
            YesNoAttribute("translate", file.Translate),
            DirectionAttribute("srcDir", file.SourceDir),
            DirectionAttribute("trgDir", file.TargetDir),
            WriteExtensionAttributes(file.ExtensionAttributes),
            file.Skeleton is null ? null : WriteVerbatim(file.Skeleton),
            file.ExtensionElements.Select(WriteExtensionElement),
            WriteNotes(file.Notes, owner),
            file.Items.Select(WriteItem));
    }

    // Mirrors what the reader accepts in a <skeleton>.
    private static void VerifySkeleton(XElement? skeleton, string owner)
    {
        if (skeleton is null)
            return;
        if (skeleton.Name != Ns + "skeleton")
            throw new NotSupportedException($"The skeleton of {owner} must be a <skeleton> element in the XLIFF namespace.");
        if (skeleton.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name != "href") is { } attribute)
            throw new NotSupportedException($"The skeleton of {owner} has the attribute '{attribute.Name.LocalName}'; <skeleton> only allows href.");
        if (skeleton.Elements().FirstOrDefault(e => e.Name.Namespace == Ns || e.Name.Namespace == XNamespace.None) is { } core)
            throw new NotSupportedException($"The skeleton of {owner} contains <{core.Name.LocalName}>; it may only contain elements from other namespaces.");
        if (skeleton.Attribute("href") is null && !skeleton.Nodes().Any(n => n is not XComment))
            throw CannotWrite(owner, "its skeleton has neither content nor an href");
    }

    // The reader rejects duplicate unit and group ids within a file, so the writer must not produce them.
    private static void VerifyUniqueItemIds(TranslationFile file)
    {
        var unitIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        Visit(file.Items);

        void Visit(IEnumerable<TranslationItem> items)
        {
            foreach (var item in items)
            {
                if (item is null)
                    throw CannotWrite($"file '{file.Id}'", "its Items contain null");
                if (item.Id is null)
                    throw CannotWrite($"file '{file.Id}'", $"a {(item is TranslationGroup ? "group" : "unit")} in it has a null Id");

                var (ids, kind) = item is TranslationGroup ? (groupIds, "group") : (unitIds, "unit");
                if (!ids.Add(item.Id))
                    throw new InvalidOperationException($"Cannot write file '{file.Id}': the {kind} id '{item.Id}' is used more than once.");
                if (item is TranslationGroup group)
                    Visit(group.Items);
            }
        }
    }

    private static XElement WriteItem(TranslationItem item) => item switch
    {
        TranslationUnit unit => WriteUnit(unit),
        TranslationGroup group => WriteGroup(group),
        _ => throw new NotSupportedException($"Unknown item type {item.GetType()}."),
    };

    private static XElement WriteGroup(TranslationGroup group)
    {
        var owner = $"group '{group.Id}'";
        VerifyItem(group, owner);

        return new XElement(Ns + "group",
            ItemAttributes(group),
            group.ExtensionElements.Select(WriteExtensionElement),
            WriteNotes(group.Notes, owner),
            group.Items.Select(WriteItem));
    }

    private static XElement WriteUnit(TranslationUnit unit)
    {
        var owner = $"unit '{unit.Id}'";
        VerifyItem(unit, owner);
        VerifyNoNulls(unit.Segments, owner, "Segments");
        foreach (var segment in unit.Segments)
        {
            if (segment.Source is null)
                throw CannotWrite(owner, $"{Describe("segment", segment.Id)} has a null Source");
            if (!Enum.IsDefined(segment.State))
                throw CannotWrite(owner, $"{Describe("segment", segment.Id)} has the undefined state {(int)segment.State}");
        }

        if (!unit.TranslatableSegments.Any())
            throw CannotWrite(owner, "it has no segment that isn't ignorable; XLIFF requires at least one <segment>");
        if (unit.OriginalData.FirstOrDefault(d => d.Value is null) is { Key: { } nullData })
            throw CannotWrite(owner, $"OriginalData '{nullData}' is null");
        if (FindTargetOrderProblem(unit) is { } orderProblem)
            throw CannotWrite(owner, orderProblem);

        VerifyInlineStructure(unit);

        return new XElement(Ns + "unit",
            ItemAttributes(unit),
            unit.ExtensionElements.Select(WriteExtensionElement),
            WriteNotes(unit.Notes, owner),
            unit.OriginalData.Count == 0 ? null : new XElement(Ns + "originalData", unit.OriginalData.Select(WriteData)),
            unit.Segments.Select(WriteSegment));
    }

    private static IEnumerable<XAttribute?> ItemAttributes(TranslationItem item) =>
    [
        new XAttribute("id", item.Id),
        OptionalAttribute("name", item.Name),
        YesNoAttribute("canResegment", item.CanResegment),
        YesNoAttribute("translate", item.Translate),
        DirectionAttribute("srcDir", item.SourceDir),
        DirectionAttribute("trgDir", item.TargetDir),
        OptionalAttribute("type", item.Type),
        .. WriteExtensionAttributes(item.ExtensionAttributes),
    ];

    private static void VerifyItem(TranslationItem item, string owner)
    {
        VerifyNoNulls(item.Notes, owner, "Notes");
        VerifyNoNulls(item.ExtensionElements, owner, "ExtensionElements");
        VerifyNoNulls(item.ExtensionAttributes, owner, "ExtensionAttributes");
    }

    private static void VerifyNoNulls<T>(IEnumerable<T?> items, string owner, string property)
        where T : class
    {
        if (items.Any(i => i is null))
            throw CannotWrite(owner, $"its {property} contain null");
    }

    private static InvalidOperationException CannotWrite(string owner, string reason) =>
        new($"Cannot write {owner}: {reason}.");

    private static string Describe(string kind, string? id) => id is null ? $"a {kind}" : $"{kind} '{id}'";

    private static XElement? WriteNotes(List<Note> notes, string owner)
    {
        if (notes.Count == 0)
            return null;
        if (FindDuplicateNoteId(notes) is { } duplicate)
            throw CannotWrite(owner, $"the note id '{duplicate}' is used more than once");

        return new XElement(Ns + "notes", notes.Select(n => WriteNote(n, owner)));
    }

    private static XElement WriteExtensionElement(XElement element)
    {
        var ns = element.Name.Namespace;
        if (ns == Ns || ns == XNamespace.None)
            throw new NotSupportedException(
                $"Extension element <{element.Name.LocalName}> must be in a namespace other than XLIFF's own.");
        return WriteVerbatim(element);
    }

    /// <summary>Copies an element the serializer doesn't interpret, so the model's instance is not attached to the output.</summary>
    private static XElement WriteVerbatim(XElement element)
    {
        var copy = new XElement(element);

        // As in WriteContent: mixed content stops XmlWriter from indenting, which could change whitespace-sensitive
        // content (e.g. the <source> of a match) that the serializer knows nothing about.
        if (copy.Nodes().Any())
            copy.AddFirst(new XText(string.Empty));
        return copy;
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
        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in unit.Segments.Select(s => s.Id).OfType<string>())
        {
            if (!segmentIds.Add(id))
                throw CannotWrite($"unit '{unit.Id}'", $"the segment id '{id}' is used more than once");
        }

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
                case InlineCode code when !Enum.IsDefined(code.Kind) || !Enum.IsDefined(code.CanReorder):
                    throw Invalid($"code '{code.Id}' has an undefined Kind or CanReorder.");
                case InlineMarker marker when !Enum.IsDefined(marker.Kind):
                    throw Invalid($"marker '{marker.Id}' has an undefined Kind.");
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
        if (segment.IsIgnorable && (segment.State != SegmentState.Initial || segment.SubState is not null || segment.CanResegment is not null))
            throw new NotSupportedException(
                $"Ignorable '{segment.Id}' has State, SubState or CanResegment set, which <ignorable> can't carry.");
        if (segment.TargetOrder is not null && segment.Target is null)
            throw new NotSupportedException($"Segment '{segment.Id}' has a TargetOrder but no Target to write it on.");
        if (segment.TargetOrder is <= 0)
            throw new InvalidOperationException($"Cannot write segment '{segment.Id}': TargetOrder must be positive.");

        var element = new XElement(Ns + (segment.IsIgnorable ? "ignorable" : "segment"),
            OptionalAttribute("id", segment.Id),
            YesNoAttribute("canResegment", segment.CanResegment));

        if (segment.State != SegmentState.Initial)
            element.Add(new XAttribute("state", FormatState(segment.State)));
        element.Add(OptionalAttribute("subState", segment.SubState));

        element.Add(WriteContent(Ns + "source", segment.Source));
        if (segment.Target is not null)
        {
            var target = WriteContent(Ns + "target", segment.Target);
            target.Add(OptionalAttribute("order", segment.TargetOrder?.ToString(CultureInfo.InvariantCulture)));
            element.Add(target);
        }

        return element;
    }

    private static XElement WriteNote(Note note, string owner)
    {
        if (note.Text is null)
            throw CannotWrite(owner, $"{Describe("note", note.Id)} has a null Text");
        if (note.Priority is < 1 or > 10)
            throw CannotWrite(owner, $"{Describe("note", note.Id)} has priority {note.Priority}; XLIFF allows 1 to 10");
        if (note.AppliesTo is { } appliesTo && !Enum.IsDefined(appliesTo))
            throw CannotWrite(owner, $"{Describe("note", note.Id)} has the undefined AppliesTo {(int)appliesTo}");
        VerifyNoNulls(note.ExtensionAttributes, owner, "note ExtensionAttributes");

        return new XElement(Ns + "note",
            OptionalAttribute("id", note.Id),
            OptionalAttribute("category", note.Category),
            OptionalAttribute("priority", note.Priority?.ToString(CultureInfo.InvariantCulture)),
            OptionalAttribute("appliesTo", note.AppliesTo switch
            {
                NoteAppliesTo.Source => "source",
                NoteAppliesTo.Target => "target",
                _ => null,
            }),
            WriteExtensionAttributes(note.ExtensionAttributes),
            note.Text);
    }

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
            if (a is null || a.Namespace is null || a.LocalName is null || a.Value is null)
                throw new InvalidOperationException("Cannot write an extension attribute that is null or has a null part.");

            try
            {
                // VerifyNCName throws ArgumentException rather than XmlException for an empty name.
                XmlConvert.VerifyNCName(a.LocalName.Length == 0 ? ":" : a.LocalName);
            }
            catch (XmlException ex)
            {
                throw new NotSupportedException($"Extension attribute name '{a.LocalName}' is not a valid XML name.", ex);
            }

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
        for (var i = IndexOfInvalidXmlChar(text, 0); i >= 0; i = IndexOfInvalidXmlChar(text, start))
        {
            if (i > start)
                parent.Add(text[start..i]);
            parent.Add(new XElement(Ns + "cp",
                new XAttribute("hex", ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture))));
            start = i + 1;
        }

        if (start < text.Length)
            parent.Add(text[start..]);
    }

    /// <summary>The index of the first character XML 1.0 can't contain (a lone surrogate counts), or -1.</summary>
    private static int IndexOfInvalidXmlChar(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (!XmlConvert.IsXmlChar(text[i]))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Content text is written with <c>&lt;cp&gt;</c>, but XLIFF has no escape for attribute values, note text or
    /// verbatim elements. Checked before anything is written, so XmlWriter can't fail halfway through the output.
    /// </summary>
    private static void VerifyRepresentable(XElement root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case XElement element:
                    foreach (var attribute in element.Attributes())
                        Verify(attribute.Value, element, $"the {attribute.Name.LocalName} attribute");
                    break;
                case XText text:
                    Verify(text.Value, text.Parent!, "the text");
                    break;
                case XComment comment when comment.Value.Contains("--", StringComparison.Ordinal) || comment.Value.EndsWith('-'):
                    throw Unrepresentable(comment.Parent!, "a comment", "'--' or a trailing '-'");
                // Text and attributes escape a carriage return as &#xD;, but comments and processing instructions
                // can't, and XML parsers turn it into a line feed.
                case XComment comment when comment.Value.Contains('\r', StringComparison.Ordinal):
                    throw Unrepresentable(comment.Parent!, "a comment", "a carriage return");
                case XProcessingInstruction instruction when instruction.Data.Contains('\r', StringComparison.Ordinal):
                    throw Unrepresentable(instruction.Parent!, "a processing instruction", "a carriage return");
                case XComment comment:
                    Verify(comment.Value, comment.Parent!, "a comment");
                    break;
                case XProcessingInstruction instruction when instruction.Data.Contains("?>", StringComparison.Ordinal):
                    throw Unrepresentable(instruction.Parent!, "a processing instruction", "'?>'");
                case XProcessingInstruction instruction:
                    Verify(instruction.Data, instruction.Parent!, "a processing instruction");
                    break;
            }
        }

        static void Verify(string value, XElement element, string what)
        {
            var i = IndexOfInvalidXmlChar(value, 0);
            if (i >= 0)
                throw Unrepresentable(element, what, $"U+{(int)value[i]:X4}");
        }
    }

    private static NotSupportedException Unrepresentable(XElement element, string what, string problem)
    {
        // Name the nearest unit, group or file, so the message points at something in the model.
        var owner = element.AncestorsAndSelf()
            .FirstOrDefault(e => e.Name == Ns + "unit" || e.Name == Ns + "group" || e.Name == Ns + "file");
        var where = owner is null ? "" : $" in {owner.Name.LocalName} '{(string?)owner.Attribute("id")}'";
        return new NotSupportedException(
            $"Cannot write {what} of <{element.Name.LocalName}>{where}: it contains {problem}, which XML can't represent there.");
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

    private static XAttribute? YesNoAttribute(string name, bool? value) =>
        value is null ? null : new XAttribute(name, value.Value ? "yes" : "no");

    private static XAttribute? DirectionAttribute(string name, string? value) =>
        value is null || IsDirection(value)
            ? OptionalAttribute(name, value)
            : throw new InvalidOperationException($"Cannot write {name} '{value}': it must be ltr, rtl or auto.");

    private static bool IsDirection(string value) => value is "ltr" or "rtl" or "auto";

    // ---------------------------------------------------------------- reading helpers

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

    /// <summary>The id spaces of one file: unit ids and group ids must each be unique within it.</summary>
    private sealed class FileContext(TranslationDocument document, string fileId)
    {
        private readonly HashSet<string> _unitIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _groupIds = new(StringComparer.Ordinal);

        public TranslationDocument Document => document;

        public string Location { get; } = $"file '{fileId}'";

        public void DefineUnit(string id)
        {
            if (!_unitIds.Add(id))
                throw new XliffFormatException($"Duplicate unit id '{id}' in file '{fileId}'.");
        }

        public void DefineGroup(string id)
        {
            if (!_groupIds.Add(id))
                throw new XliffFormatException($"Duplicate group id '{id}' in file '{fileId}'.");
        }
    }

    /// <summary>
    /// Reads attributes and keeps track of which ones were used, so unknown XLIFF attributes fail loudly
    /// and attributes from other namespaces are kept as <see cref="ExtensionAttr"/>s.
    /// </summary>
    /// <param name="location">Where the element is, for messages (e.g. <c>unit 'u1'</c>); null for top-level elements.</param>
    private sealed class AttributeReader(XElement element, string? location)
    {
        private readonly HashSet<XName> _read = [];

        private string Where => location is null
            ? $"<{element.Name.LocalName}>"
            : $"<{element.Name.LocalName}> in {location}";

        public string? Optional(XName name)
        {
            _read.Add(name);
            return (string?)element.Attribute(name);
        }

        public string Required(XName name) =>
            Optional(name)
            ?? throw new XliffFormatException($"{Where} is missing the required '{name.LocalName}' attribute.");

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

        public string? Direction(string name)
        {
            var value = Optional(name);
            return value is null || IsDirection(value) ? value : throw Invalid(name, value);
        }

        public int? PositiveInteger(string name)
        {
            var text = Optional(name);
            if (text is null)
                return null;

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw Invalid(name, text);
        }

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
                    throw new XliffFormatException($"Unsupported attribute '{attribute.Name.LocalName}' on {Where}.");

                result.Add(new ExtensionAttr(ns.NamespaceName, attribute.Name.LocalName, attribute.Value));
            }

            return result;
        }

        /// <summary>For elements that can't carry extension attributes.</summary>
        public void VerifyNoneLeft()
        {
            var extensions = ExtensionAttributes();
            if (extensions.Count > 0)
                throw new XliffFormatException($"Unsupported attribute '{extensions[0].LocalName}' on {Where}.");
        }

        public XliffFormatException Invalid(string name, string value, string? reason = null) =>
            new($"Invalid {name} value '{value}' on {Where}{(reason is null ? "" : $": {reason}")}.");
    }
}
