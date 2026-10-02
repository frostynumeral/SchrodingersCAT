using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

public class Xliff20InlineReadingTests : Xliff20SerializerTestBase
{
    [Fact]
    public async Task Read_MapsCodesMarkersAndOriginalData()
    {
        var unit = await ReadUnitAsync("""
            <originalData>
              <data id="d1">&lt;br/&gt;</data>
              <data id="d2">&lt;b&gt;</data>
              <data id="d3">&lt;/b&gt;</data>
            </originalData>
            <segment>
              <source>Line<ph id="1" dataRef="d1" equiv=" "/><pc id="2" type="fmt" subType="xlf:b" dataRefStart="d2" dataRefEnd="d3">bold</pc> <mrk id="m1" translate="no">ACME</mrk></source>
            </segment>
            """);

        Assert.Equal(new InlineContent(
        [
            new InlineText("Line"),
            new InlineCode("1", InlineCodeKind.Standalone) { DataRef = "d1", Equiv = " " },
            new InlineCode("2", InlineCodeKind.Opening) { Type = "fmt", SubType = "xlf:b", CanOverlap = false, DataRef = "d2" },
            new InlineText("bold"),
            new InlineCode("2", InlineCodeKind.Closing) { Type = "fmt", SubType = "xlf:b", CanOverlap = false, DataRef = "d3" },
            new InlineText(" "),
            new InlineMarker("m1", InlineMarkerKind.Opening) { Translate = false },
            new InlineText("ACME"),
            new InlineMarker("m1", InlineMarkerKind.Closing),
        ]), unit.Segments[0].Source);

        Assert.Equal("Line bold ACME", unit.Segments[0].Source.ToEquivText());
        Assert.Equal(new Dictionary<string, string> { ["d1"] = "<br/>", ["d2"] = "<b>", ["d3"] = "</b>" }, unit.OriginalData);
    }

    [Fact]
    public async Task Read_PcAndScEcWithTheSameAttributesLoadIdentically()
    {
        var pc = await ReadSourceAsync("""<pc id="1" type="fmt" canOverlap="yes">x</pc>""");
        var scEc = await ReadSourceAsync("""<sc id="1" type="fmt"/>x<ec startRef="1" type="fmt"/>""");

        Assert.Equal(pc, scEc);
    }

    [Fact]
    public async Task Read_ResolvesCanOverlapToTheElementDefault()
    {
        var pc = await ReadSourceAsync("""<pc id="1">x</pc>""");
        var scEc = await ReadSourceAsync("""<sc id="1"/>x<ec startRef="1"/>""");

        Assert.All(pc.OfType<InlineCode>(), c => Assert.False(c.CanOverlap));
        Assert.All(scEc.OfType<InlineCode>(), c => Assert.True(c.CanOverlap));
    }

    [Fact]
    public async Task Read_PcStartingWithFirstNoEndsWithNo()
    {
        var codes = await ReadCodesAsync("""<pc id="1" canReorder="firstNo">x</pc>""");

        Assert.Equal(CanReorder.FirstNo, codes[0].CanReorder);
        Assert.Equal(CanReorder.No, codes[1].CanReorder);
    }

    [Fact]
    public async Task Read_PairsCanSpanSegments()
    {
        var unit = await ReadUnitAsync("""
            <segment id="s1"><source><sc id="1"/>First. <sm id="m1" type="term"/>Sec</source></segment>
            <ignorable><source> </source></ignorable>
            <segment id="s2"><source>ond<em startRef="m1"/>.<ec startRef="1"/></source></segment>
            """);

        Assert.Equal(new InlineContent(
        [
            new InlineCode("1", InlineCodeKind.Opening) { CanOverlap = true },
            new InlineText("First. "),
            new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" },
            new InlineText("Sec"),
        ]), unit.Segments[0].Source);
        Assert.Equal(new InlineContent(
        [
            new InlineText("ond"),
            new InlineMarker("m1", InlineMarkerKind.Closing),
            new InlineText("."),
            new InlineCode("1", InlineCodeKind.Closing) { CanOverlap = true },
        ]), unit.Segments[2].Source);
    }

    [Fact]
    public async Task Read_IsolatedCodes()
    {
        var codes = await ReadCodesAsync("""<sc id="1" isolated="yes"/>a<ec id="2" isolated="yes" dir="rtl"/>""");

        Assert.All(codes, c => Assert.True(c.IsIsolated));
        Assert.Equal(["1", "2"], codes.Select(c => c.Id));
        Assert.Equal("rtl", codes[1].Dir);
    }

    [Fact]
    public async Task Read_TargetReusesSourceIds()
    {
        var unit = await ReadUnitAsync("""
            <segment>
              <source>Hello <ph id="1"/></source>
              <target>Szia <ph id="1"/></target>
            </segment>
            """);

        Assert.Equal(unit.Segments[0].Source[1], unit.Segments[0].Target![1]);
    }

    [Fact]
    public async Task Read_CodesInIgnorables()
    {
        var unit = await ReadUnitAsync("""
            <segment><source>One.</source></segment>
            <ignorable><source><ph id="1"/></source></ignorable>
            """);

        Assert.True(unit.Segments[1].Source.HasTags);
    }

    [Fact]
    public async Task Read_DecodesCodePoints()
    {
        var unit = await ReadUnitAsync("""
            <originalData><data id="d1">x<cp hex="FFFE"/></data></originalData>
            <segment><source>a<cp hex="1"/>b<ph id="1" dataRef="d1"/><cp hex="1F600"/></source></segment>
            """);

        Assert.Equal("a\u0001b\U0001F600", unit.Segments[0].Source.ToPlainText());
        Assert.Equal("x￾", unit.OriginalData["d1"]);
    }

    [Fact]
    public async Task Read_KeepsExtensionAttributes()
    {
        var codes = await ReadCodesAsync("""<ph id="1" fs:fs="br"/><pc id="2" fs:fs="b">x</pc>""");

        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "br")], codes[0].ExtensionAttributes);
        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "b")], codes[1].ExtensionAttributes);
    }

    [Theory]
    [InlineData("""<segment><source><ph id="1"/><ph id="1"/></source></segment>""")]
    [InlineData("""<segment id="1"><source><ph id="1"/></source></segment>""")]
    [InlineData("""<segment><source><ph id="1"/></source></segment><ignorable><source><ph id="1"/></source></ignorable>""")]
    [InlineData("""<segment id="s"><source>a</source></segment><segment id="s"><source>b</source></segment>""")]
    [InlineData("""<originalData><data id="d1">a</data><data id="d1">b</data></originalData><segment><source>x</source></segment>""")]
    [InlineData("""<segment><source><ph id="1" dataRef="d1"/></source></segment>""")]
    public async Task Read_RejectsDuplicateOrDanglingIds(string unitContent)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadUnitAsync(unitContent));
    }

    [Theory]
    [InlineData("""<sc id="1"/>""")]
    [InlineData("""<ec startRef="1"/>""")]
    [InlineData("""<sc id="1" isolated="yes"/><ec startRef="1"/>""")]
    [InlineData("""<sm id="1"/>""")]
    [InlineData("""<em startRef="1"/>""")]
    public async Task Read_RejectsUnmatchedPairs(string sourceContent)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadSourceAsync(sourceContent));
    }

    [Theory]
    [InlineData("""<b>x</b>""")]
    [InlineData("""<ph/>""")]
    [InlineData("""<ph id="1">x</ph>""")]
    [InlineData("""<ph id="1" foo="x"/>""")]
    [InlineData("""<ph id="1" canCopy="maybe"/>""")]
    [InlineData("""<sc id="1"/><ec startRef="1" dir="rtl"/>""")]
    [InlineData("""<cp hex="zz"/>""")]
    [InlineData("""<cp hex="110000"/>""")]
    public async Task Read_RejectsInvalidInlineMarkup(string sourceContent)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadSourceAsync(sourceContent));
    }

    /// <summary>Reads a unit with a single segment and returns that segment's source.</summary>
    private async Task<InlineContent> ReadSourceAsync(string sourceContent) =>
        (await ReadUnitAsync($"<segment><source>{sourceContent}</source></segment>")).Segments.Single().Source;

    private async Task<List<InlineCode>> ReadCodesAsync(string sourceContent) =>
        (await ReadSourceAsync(sourceContent)).OfType<InlineCode>().ToList();
}
