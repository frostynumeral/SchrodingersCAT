using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// XLIFF 2.0 constraints that span several elements: trgLang when there are targets, note ids per parent,
/// and target order. The reader and the writer share the checks, so both sides are tested.
/// </summary>
public class Xliff20SpecConstraintTests : Xliff20SerializerTestBase
{
    // ---------------------------------------------------------------- trgLang

    [Theory]
    [InlineData("<segment><source>a</source><target>b</target></segment>")]
    [InlineData("<segment><source>a</source></segment><ignorable><source> </source><target> </target></ignorable>")]
    public async Task Read_RejectsTargetsWithoutTrgLang(string unitContent)
    {
        var xml = $"""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en">
              <file id="f1"><unit id="u1">{unitContent}</unit></file>
            </xliff>
            """;

        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(xml));
    }

    [Fact]
    public async Task Read_AcceptsNoTrgLangWithoutTargets()
    {
        const string xml = """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en">
              <file id="f1"><unit id="u1"><segment><source>a</source></segment></unit></file>
            </xliff>
            """;

        Assert.Null((await ReadAsync(xml)).TargetLanguage);
    }

    [Fact]
    public async Task Write_RejectsTargetsWithoutTargetLanguage()
    {
        var document = DocumentWith(new InlineText("a"));
        document.Units.Single().Segments[0].Target = InlineContent.FromText("b");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(document));

        document.TargetLanguage = "hu";
        Assert.Contains("trgLang=\"hu\"", await WriteAsync(document));
    }

    // ---------------------------------------------------------------- note ids

    [Theory]
    [InlineData("""<unit id="u1"><notes><note id="n1">a</note><note id="n1">b</note></notes><segment><source>a</source></segment></unit>""")]
    [InlineData("""<group id="g1"><notes><note id="n1">a</note><note id="n1">b</note></notes></group>""")]
    [InlineData("""<notes><note id="n1">a</note><note id="n1">b</note></notes><unit id="u1"><segment><source>a</source></segment></unit>""")]
    public async Task Read_RejectsDuplicateNoteIdsInOneParent(string fileContent)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(WrapUnits(fileContent)));
    }

    [Fact]
    public async Task Read_AcceptsTheSameNoteIdInDifferentParents()
    {
        var file = (await ReadAsync(WrapUnits("""
            <notes><note id="n1">file</note></notes>
            <group id="g1">
              <notes><note id="n1">group</note></notes>
              <unit id="u1"><notes><note id="n1">unit</note><note>no id</note><note>no id either</note></notes><segment><source>a</source></segment></unit>
            </group>
            """))).Files.Single();

        Assert.Equal(["file", "group", "unit"], new[] { file.Notes[0], ((TranslationGroup)file.Items[0]).Notes[0], file.Units.Single().Notes[0] }.Select(n => n.Text));
    }

    public static TheoryData<string, Action<TranslationDocument>> DuplicateNoteIds => new()
    {
        { "unit", d => d.Units.Single().Notes.AddRange([Note("n1"), Note("n1")]) },
        { "file", d => d.Files[0].Notes.AddRange([Note("n1"), Note("n1")]) },
        { "group", d => d.Files[0].Items.Add(new TranslationGroup { Id = "g1", Notes = { Note("n1"), Note("n1") } }) },
    };

    [Theory]
    [MemberData(nameof(DuplicateNoteIds))]
    public async Task Write_RejectsDuplicateNoteIdsInOneParent(string parent, Action<TranslationDocument> change)
    {
        _ = parent; // only names the test case
        var document = DocumentWith(new InlineText("a"));
        change(document);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(document));
    }

    [Fact]
    public async Task Write_AcceptsTheSameNoteIdInDifferentParents()
    {
        var document = DocumentWith(new InlineText("a"));
        document.Files[0].Notes.Add(Note("n1"));
        document.Units.Single().Notes.AddRange([Note("n1"), Note(null), Note(null)]);

        var reread = await ReadAsync(await WriteAsync(document));

        Assert.Equal(["n1", null, null], reread.Units.Single().Notes.Select(n => n.Id));
    }

    // ---------------------------------------------------------------- target order

    [Theory]
    [InlineData("""<segment><source>a</source><target order="2">A</target></segment>""")]
    [InlineData("""<segment><source>a</source><target order="1">A</target></segment><segment><source>b</source><target order="1">B</target></segment>""")]
    [InlineData("""<segment><source>a</source><target order="4">A</target></segment><ignorable><source> </source></ignorable><segment><source>b</source></segment>""")]
    public async Task Read_RejectsInvalidTargetOrder(string unitContent)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadUnitAsync(unitContent));
    }

    [Fact]
    public async Task Read_AcceptsTargetOrderUpToTheSegmentAndIgnorableCount()
    {
        var unit = await ReadUnitAsync("""
            <segment><source>a</source><target order="3">A</target></segment>
            <ignorable><source> </source><target order="2"> </target></ignorable>
            <segment><source>b</source><target order="1">B</target></segment>
            """);

        Assert.Equal([3, 2, 1], unit.Segments.Select(s => s.TargetOrder));
    }

    public static TheoryData<string, int?[]> InvalidTargetOrders => new()
    {
        { "past the segment count", [3, null] },
        { "duplicate", [1, 1] },
    };

    [Theory]
    [MemberData(nameof(InvalidTargetOrders))]
    public async Task Write_RejectsInvalidTargetOrder(string name, int?[] orders)
    {
        _ = name; // only names the test case
        var document = DocumentWithTargets(orders);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(document));
    }

    [Fact]
    public async Task Write_AcceptsAReorderedUnit()
    {
        var document = DocumentWithTargets([2, 1]);

        var reread = await ReadAsync(await WriteAsync(document));

        Assert.Equal([2, 1], reread.Units.Single().Segments.Select(s => s.TargetOrder));
    }

    private static Note Note(string? id) => new() { Id = id, Text = "n" };

    private static TranslationDocument DocumentWithTargets(int?[] orders)
    {
        var document = DocumentWith(new InlineText("a"));
        document.TargetLanguage = "hu";
        var segments = document.Units.Single().Segments;
        segments.Clear();
        foreach (var order in orders)
            segments.Add(new Segment { Source = InlineContent.FromText("a"), Target = InlineContent.FromText("A"), TargetOrder = order });
        return document;
    }
}
