using System.Xml.Linq;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// The writer must refuse what the reader would reject, with <see cref="InvalidOperationException"/> or
/// <see cref="NotSupportedException"/>, instead of writing a file that won't re-read or failing inside XmlWriter.
/// </summary>
public class Xliff20WriterValidationTests : Xliff20SerializerTestBase
{
    private static readonly XNamespace Xliff = "urn:oasis:names:tc:xliff:document:2.0";

    public static TheoryData<string, Action<TranslationDocument>> InvalidModels => new()
    {
        { "unit with only ignorables", d => Unit(d).Segments[0].IsIgnorable = true },
        { "unit with no segments", d => Unit(d).Segments.Clear() },
        { "note priority 0", d => Unit(d).Notes.Add(new Note { Text = "n", Priority = 0 }) },
        { "note priority 11", d => Unit(d).Notes.Add(new Note { Text = "n", Priority = 11 }) },
        { "file with no units or groups", d => d.Files[0].Items.Clear() },
        { "document with no files", d => d.Files.Clear() },
        { "undefined segment state", d => Unit(d).Segments[0].State = (SegmentState)42 },
        { "undefined note appliesTo", d => Unit(d).Notes.Add(new Note { Text = "n", AppliesTo = (NoteAppliesTo)42 }) },
        { "undefined code kind", d => SetSource(d, new InlineCode("1", (InlineCodeKind)42)) },
        { "undefined marker kind", d => SetSource(d, new InlineMarker("1", (InlineMarkerKind)42)) },
        { "undefined canReorder", d => SetSource(d, new InlineCode("1", InlineCodeKind.Standalone) { CanReorder = (CanReorder)42 }) },
        { "duplicate segment ids", d => Unit(d).Segments.Add(new Segment { Id = "s1", Source = InlineContent.FromText("b") }) },
        { "null source language", d => d.SourceLanguage = null! },
        { "null file id", d => d.Files[0].Id = null! },
        { "null unit id", d => Unit(d).Id = null! },
        { "null group id", d => d.Files[0].Items.Add(new TranslationGroup { Id = null! }) },
        { "null note text", d => Unit(d).Notes.Add(new Note { Text = null! }) },
        { "null segment source", d => Unit(d).Segments[0].Source = null! },
        { "null item", d => d.Files[0].Items.Add(null!) },
        { "null note", d => Unit(d).Notes.Add(null!) },
        { "null segment", d => Unit(d).Segments.Add(null!) },
        { "null file", d => d.Files.Add(null!) },
        { "null extension element", d => Unit(d).ExtensionElements.Add(null!) },
        { "null extension attribute", d => Unit(d).ExtensionAttributes.Add(null!) },
        { "null originalData value", d => Unit(d).OriginalData["d1"] = null! },
        { "empty skeleton", d => d.Files[0].Skeleton = new XElement(Xliff + "skeleton") },
    };

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public async Task Write_RejectsModelsTheReaderWouldReject(string name, Action<TranslationDocument> change)
    {
        _ = name; // only names the test case
        var document = Valid();
        change(document);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(document));
    }

    public static TheoryData<string, Action<TranslationDocument>> UnrepresentableModels => new()
    {
        { "control character in note text", d => Unit(d).Notes.Add(new Note { Text = "bell\u0007" }) },
        { "lone surrogate in note text", d => Unit(d).Notes.Add(new Note { Text = "\uD800" }) },
        { "control character in unit name", d => Unit(d).Name = "a\u0001" },
        { "control character in subState", d => Unit(d).Segments[0].SubState = "x:\u0002" },
        { "control character in a code's disp", d => SetSource(d, new InlineCode("1", InlineCodeKind.Standalone) { Disp = "\u0003" }) },
        { "control character in an originalData id", d => SetSource(d, new InlineText("x"), "\u0004") },
        { "control character in an extension attribute", d => Unit(d).ExtensionAttributes.Add(new ExtensionAttr("urn:x", "a", "\u0005")) },
        { "control character in an extension element", d => Unit(d).ExtensionElements.Add(new XElement(XName.Get("e", "urn:x"), "\u0006")) },
        { "invalid extension attribute name", d => Unit(d).ExtensionAttributes.Add(new ExtensionAttr("urn:x", "not a name", "1")) },
        { "XLIFF element in the skeleton", d => d.Files[0].Skeleton = new XElement(Xliff + "skeleton", new XElement(Xliff + "unit")) },
        { "unknown attribute on the skeleton", d => d.Files[0].Skeleton = new XElement(Xliff + "skeleton", new XAttribute("foo", "1"), "x") },
    };

    [Theory]
    [MemberData(nameof(UnrepresentableModels))]
    public async Task Write_RejectsModelsXliffCannotRepresent(string name, Action<TranslationDocument> change)
    {
        _ = name; // only names the test case
        var document = Valid();
        change(document);

        await Assert.ThrowsAsync<NotSupportedException>(() => WriteAsync(document));
    }

    [Fact]
    public async Task Write_NothingIsWrittenWhenValidationFails()
    {
        var document = Valid();
        Unit(document).Notes.Add(new Note { Text = "bell\u0007" });
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<NotSupportedException>(() => Serializer.WriteAsync(document, stream));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Write_AcceptsTheBoundaries()
    {
        var document = Valid();
        Unit(document).Notes.Add(new Note { Text = "tab\tnewline\n\U0001F600", Priority = 1 });
        Unit(document).Notes.Add(new Note { Text = "low", Priority = 10 });
        Unit(document).Segments.Add(new Segment { Source = InlineContent.FromText(" "), IsIgnorable = true });
        document.Files[0].Skeleton = new XElement(Xliff + "skeleton", new XAttribute("href", "f1.skl"));

        var reread = await ReadAsync(await WriteAsync(document));

        Assert.Equal([1, 10], reread.Units.Single().Notes.Select(n => n.Priority));
        Assert.Equal("tab\tnewline\n\U0001F600", reread.Units.Single().Notes[0].Text);
    }

    [Theory]
    [InlineData("""<xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en"><file id="f1"/></xliff>""")]
    [InlineData("""<xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en"><file id="f1"><notes><note>n</note></notes></file></xliff>""")]
    [InlineData("""<xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en"/>""")]
    public async Task Read_RejectsEmptyFilesAndDocuments(string xml)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(xml));
    }

    private static TranslationDocument Valid()
    {
        var document = DocumentWith(new InlineText("a"));
        Unit(document).Segments[0].Id = "s1";
        return document;
    }

    private static TranslationUnit Unit(TranslationDocument document) => document.Units.Single();

    private static void SetSource(TranslationDocument document, InlineElement element, string? dataRef = null)
    {
        var unit = Unit(document);
        if (dataRef is not null)
            unit.OriginalData[dataRef] = "<br/>";
        unit.Segments[0].Source = new InlineContent([element]);
    }
}
