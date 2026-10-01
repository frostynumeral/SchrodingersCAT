using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

public class Xliff20SerializerTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en-US" trgLang="hu-HU">
          <file id="f1" original="strings.resx">
            <group id="g1">
              <unit id="u1" name="Greeting">
                <notes>
                  <note category="context" appliesTo="source">Shown on the start page.</note>
                </notes>
                <segment id="s1" state="translated">
                  <source>Hello world.</source>
                  <target>Helló világ.</target>
                </segment>
                <ignorable>
                  <source> </source>
                </ignorable>
                <segment id="s2">
                  <source>Welcome!</source>
                </segment>
              </unit>
            </group>
            <unit id="u2">
              <segment state="final" subState="cat:locked">
                <source>OK</source>
                <target>OK</target>
              </segment>
            </unit>
          </file>
        </xliff>
        """;

    private readonly Xliff20Serializer _serializer = new();

    [Fact]
    public async Task Read_MapsXliffToModel()
    {
        var doc = await ReadAsync(Sample);

        Assert.Equal("en-US", doc.SourceLanguage);
        Assert.Equal("hu-HU", doc.TargetLanguage);

        var file = Assert.Single(doc.Files);
        Assert.Equal("strings.resx", file.Original);
        Assert.Equal(["u1", "u2"], file.Units.Select(u => u.Id)); // group flattened

        var u1 = file.Units[0];
        Assert.Equal("Greeting", u1.Name);
        Assert.Equal(3, u1.Segments.Count);
        Assert.Equal(2, u1.TranslatableSegments.Count());
        Assert.Equal(Text("Hello world. Welcome!"), u1.Source);
        Assert.Equal(Text("Helló világ. Welcome!"), u1.Target);

        var s1 = u1.Segments[0];
        Assert.Equal("s1", s1.Id);
        Assert.Equal(SegmentState.Translated, s1.State);

        var s2 = u1.Segments[2];
        Assert.Equal(SegmentState.Initial, s2.State);
        Assert.False(s2.HasTarget);

        var note = Assert.Single(u1.Notes);
        Assert.Equal("Shown on the start page.", note.Text);
        Assert.Equal("context", note.Category);
        Assert.Equal(NoteAppliesTo.Source, note.AppliesTo);

        var u2Segment = Assert.Single(file.Units[1].Segments);
        Assert.Equal(SegmentState.Final, u2Segment.State);
        Assert.Equal("cat:locked", u2Segment.SubState);
    }

    [Fact]
    public async Task EditAndRoundTrip_PreservesEverythingInTheModel()
    {
        var doc = await ReadAsync(Sample);

        // Simulate the translator's work.
        var segment = doc.Units.First().Segments[2];
        segment.Target = Text("Üdvözöljük!");
        segment.State = SegmentState.Translated;

        var reread = await ReadAsync(await WriteAsync(doc));

        var unit = reread.Units.First();
        Assert.Equal(Text("Helló világ. Üdvözöljük!"), unit.Target);
        Assert.Equal(SegmentState.Translated, unit.Segments[2].State);
        Assert.True(unit.Segments[1].IsIgnorable);
        Assert.Equal(Text(" "), unit.Segments[1].Source);
        Assert.Equal("cat:locked", reread.Units.Last().Segments[0].SubState);
        Assert.Equal(NoteAppliesTo.Source, unit.Notes[0].AppliesTo);
    }

    [Fact]
    public async Task Write_MarksSignificantWhitespaceAsPreserved()
    {
        var doc = new TranslationDocument { SourceLanguage = "en" };
        var file = new TranslationFile { Id = "f1" };
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = Text("  indented"), Target = Text("plain text") });
        file.Units.Add(unit);
        doc.Files.Add(file);

        var xml = await WriteAsync(doc);

        Assert.Contains("<source xml:space=\"preserve\">  indented</source>", xml);
        Assert.Contains("<target>plain text</target>", xml);
        Assert.Equal(Text("  indented"), (await ReadAsync(xml)).Units.Single().Segments[0].Source);
    }

    [Fact]
    public async Task Write_KeepsEmptyTargetDistinctFromMissingTarget()
    {
        var doc = await ReadAsync(Sample);
        doc.Units.First().Segments[0].Target = InlineContent.Empty;

        var unit = (await ReadAsync(await WriteAsync(doc))).Units.First();

        Assert.Equal(InlineContent.Empty, unit.Segments[0].Target);
        Assert.Null(unit.Segments[2].Target);
    }

    [Fact]
    public async Task Read_RejectsXliff12()
    {
        const string xml = """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2">
              <file source-language="en" datatype="plaintext" original="a.txt"><body/></file>
            </xliff>
            """;

        var ex = await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(xml));
        Assert.Contains("1.2", ex.Message);
    }

    [Fact]
    public async Task Read_RejectsDuplicateUnitIds()
    {
        var xml = Sample.Replace("<unit id=\"u2\">", "<unit id=\"u1\">");

        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(xml));
    }

    [Fact]
    public async Task WriteFileAsync_ReplacesFileWithoutLeavingTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.xlf");
        try
        {
            await File.WriteAllTextAsync(path, "old content");

            await _serializer.WriteFileAsync(await ReadAsync(Sample), path);

            Assert.Equal(2, (await _serializer.ReadFileAsync(path)).Units.Count());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private async Task<TranslationDocument> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await _serializer.ReadAsync(stream);
    }

    private async Task<string> WriteAsync(TranslationDocument doc)
    {
        using var stream = new MemoryStream();
        await _serializer.WriteAsync(doc, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
