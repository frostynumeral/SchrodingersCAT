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
/// <item>Inline codes (<c>&lt;ph&gt;</c>, <c>&lt;pc&gt;</c>, <c>&lt;mrk&gt;</c>, …) are rejected with an
/// <see cref="XliffFormatException"/> rather than silently dropped.</item>
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

        // Segments and ignorables are siblings and their order matters (it rebuilds the full text).
        foreach (var child in unitElement.Elements())
        {
            if (child.Name == Ns + "segment")
                unit.Segments.Add(ReadSegment(child, unit.Id, isIgnorable: false));
            else if (child.Name == Ns + "ignorable")
                unit.Segments.Add(ReadSegment(child, unit.Id, isIgnorable: true));
        }

        if (!unit.TranslatableSegments.Any())
            throw new XliffFormatException($"Unit '{unit.Id}' contains no <segment>.");

        return unit;
    }

    private static Segment ReadSegment(XElement element, string unitId, bool isIgnorable)
    {
        var sourceElement = element.Element(Ns + "source")
            ?? throw new XliffFormatException($"A segment in unit '{unitId}' has no <source>.");
        var targetElement = element.Element(Ns + "target");

        return new Segment
        {
            Id = (string?)element.Attribute("id"),
            Source = ReadPlainText(sourceElement, unitId),
            Target = targetElement is null ? null : ReadPlainText(targetElement, unitId),
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

    private static string ReadPlainText(XElement element, string unitId)
    {
        // Fail loudly: flattening inline codes to text would corrupt the file on save.
        var inline = element.Elements().FirstOrDefault();
        if (inline is not null)
            throw new XliffFormatException(
                $"Unit '{unitId}' contains inline code <{inline.Name.LocalName}>, which is not supported yet.");

        return element.Value;
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

    private static XElement WriteUnit(TranslationUnit unit) =>
        new(Ns + "unit",
            new XAttribute("id", unit.Id),
            OptionalAttribute("name", unit.Name),
            unit.Notes.Count == 0 ? null : new XElement(Ns + "notes", unit.Notes.Select(WriteNote)),
            unit.Segments.Select(WriteSegment));

    private static XElement WriteSegment(Segment segment)
    {
        var element = new XElement(Ns + (segment.IsIgnorable ? "ignorable" : "segment"),
            OptionalAttribute("id", segment.Id));

        if (!segment.IsIgnorable && segment.State != SegmentState.Initial)
            element.Add(new XAttribute("state", FormatState(segment.State)));
        if (!segment.IsIgnorable && segment.SubState is not null)
            element.Add(new XAttribute("subState", segment.SubState));

        element.Add(WriteText(Ns + "source", segment.Source));
        if (segment.Target is not null)
            element.Add(WriteText(Ns + "target", segment.Target));

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

    private static XElement WriteText(XName name, string text)
    {
        var element = new XElement(name, text);

        // Tell other tools not to normalize whitespace that is significant for the translation.
        if (HasSignificantWhitespace(text))
            element.Add(new XAttribute(XmlSpace, "preserve"));

        return element;
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
}
