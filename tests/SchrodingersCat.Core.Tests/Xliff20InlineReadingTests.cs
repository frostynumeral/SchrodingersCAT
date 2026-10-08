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

    [Fact]
    public async Task Read_PcSplitsStartAndEndAttributesBetweenItsHalves()
    {
        var codes = await ReadCodesAsync("""
            <pc id="1" dir="rtl" fs:fs="b" dispStart="[b]" dispEnd="[/b]" equivStart="(" equivEnd=")" subFlowsStart="u2" subFlowsEnd="u3">x</pc>
            """);

        Assert.Equal(
            new InlineCode("1", InlineCodeKind.Opening)
            {
                CanOverlap = false,
                Dir = "rtl",
                Disp = "[b]",
                Equiv = "(",
                SubFlows = "u2",
                ExtensionAttributes = [new ExtensionAttr(FsNamespace, "fs", "b")],
            },
            codes[0]);
        Assert.Equal(
            new InlineCode("1", InlineCodeKind.Closing) { CanOverlap = false, Disp = "[/b]", Equiv = ")", SubFlows = "u3" },
            codes[1]);
    }

    [Fact]
    public async Task Read_PcRepeatsPairLevelAttributesOnItsClosingHalf()
    {
        var codes = await ReadCodesAsync("""
            <ph id="0"/><pc id="1" type="fmt" subType="xlf:i" copyOf="0" canCopy="no" canDelete="no" canReorder="no" canOverlap="yes">x</pc>
            """);

        Assert.All(codes.Skip(1), c =>
        {
            Assert.Equal("fmt", c.Type);
            Assert.Equal("xlf:i", c.SubType);
            Assert.Equal("0", c.CopyOf);
            Assert.False(c.CanCopy);
            Assert.False(c.CanDelete);
            Assert.Equal(CanReorder.No, c.CanReorder);
            Assert.True(c.CanOverlap);
        });
    }

    [Fact]
    public async Task Read_MapsMarkerAttributes()
    {
        var markers = await ReadMarkersAsync("""
            <mrk id="m1" type="comment" value="Check this" fs:fs="i">a</mrk> <sm id="m2" type="term" ref="#t1" translate="yes"/>b<em startRef="m2"/>
            """);

        Assert.Equal(
            [
                new InlineMarker("m1", InlineMarkerKind.Opening)
                {
                    Type = "comment",
                    Value = "Check this",
                    ExtensionAttributes = [new ExtensionAttr(FsNamespace, "fs", "i")],
                },
                new InlineMarker("m1", InlineMarkerKind.Closing),
                new InlineMarker("m2", InlineMarkerKind.Opening) { Type = "term", Ref = "#t1", Translate = true },
                new InlineMarker("m2", InlineMarkerKind.Closing),
            ],
            markers);
    }

    [Fact]
    public async Task Read_NestedPcAndMrkFlattenInDocumentOrder()
    {
        var source = await ReadSourceAsync("""<pc id="1">a<mrk id="m1" translate="no">b<ph id="2"/></mrk></pc>""");

        Assert.Equal(new InlineContent(
        [
            new InlineCode("1", InlineCodeKind.Opening) { CanOverlap = false },
            new InlineText("a"),
            new InlineMarker("m1", InlineMarkerKind.Opening) { Translate = false },
            new InlineText("b"),
            new InlineCode("2", InlineCodeKind.Standalone),
            new InlineMarker("m1", InlineMarkerKind.Closing),
            new InlineCode("1", InlineCodeKind.Closing) { CanOverlap = false },
        ]), source);
    }

    [Theory]
    [InlineData("""<segment><source><ph id="1"/><ph id="1"/></source></segment>""", "Duplicate id '1' in the source")]
    [InlineData("""<segment id="1"><source><ph id="1"/></source></segment>""", "Duplicate id '1' in the source")]
    [InlineData("""<segment><source><ph id="1"/></source></segment><ignorable><source><ph id="1"/></source></ignorable>""", "Duplicate id '1' in the source")]
    [InlineData("""<segment id="s"><source>a</source></segment><segment id="s"><source>b</source></segment>""", "Duplicate segment id 's'")]
    [InlineData("""<originalData><data id="d1">a</data><data id="d1">b</data></originalData><segment><source>x</source></segment>""", "Duplicate <data> id 'd1'")]
    [InlineData("""<segment><source><ph id="1" dataRef="d1"/></source></segment>""", "dataRef 'd1' in unit 'u1' does not match any <data>")]
    [InlineData("""<segment><source><pc id="1" dataRefEnd="d1">x</pc></source></segment>""", "dataRef 'd1' in unit 'u1' does not match any <data>")]
    [InlineData("""<segment><source><mrk id="1">a</mrk><ph id="1"/></source></segment>""", "Duplicate id '1' in the source")]
    public async Task Read_RejectsDuplicateOrDanglingIds(string unitContent, string expectedMessage)
    {
        var exception = await Assert.ThrowsAsync<XliffFormatException>(() => ReadUnitAsync(unitContent));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<sc id="1"/>""", """<sc id="1"> in the source of unit 'u1' has no matching <ec>""")]
    [InlineData("""<ec startRef="1"/>""", """<ec startRef="1"> in the source of unit 'u1' has no matching <sc>""")]
    [InlineData("""<sc id="1" isolated="yes"/><ec startRef="1"/>""", """<ec startRef="1"> in the source of unit 'u1' has no matching <sc>""")]
    [InlineData("""<sm id="1"/>""", """<sm id="1"> in the source of unit 'u1' has no matching <em>""")]
    [InlineData("""<em startRef="1"/>""", """<em startRef="1"> in the source of unit 'u1' has no matching <sm>""")]
    [InlineData("""<em startRef="1"/><sm id="1"/>""", """<em startRef="1"> in the source of unit 'u1' has no matching <sm>""")]
    [InlineData("""<sc id="1"/><ec startRef="2"/>""", """<ec startRef="2"> in the source of unit 'u1' has no matching <sc>""")]
    public async Task Read_RejectsUnmatchedPairs(string sourceContent, string expectedMessage)
    {
        var exception = await Assert.ThrowsAsync<XliffFormatException>(() => ReadSourceAsync(sourceContent));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<b>x</b>""", "contains <b> in its source")]
    [InlineData("""<ph/>""", "missing the required 'id' attribute")]
    [InlineData("""<ph id="1">x</ph>""", "<ph> in unit 'u1' must be empty")]
    [InlineData("""<ph id="1" foo="x"/>""", "Unsupported attribute 'foo' on <ph>")]
    [InlineData("""<ph id="1" canCopy="maybe"/>""", "Invalid canCopy value 'maybe'")]
    [InlineData("""<sc id="1"/><ec startRef="1" dir="rtl"/>""", "Unsupported attribute 'dir' on <ec>")]
    [InlineData("""<pc>x</pc>""", "missing the required 'id' attribute")]
    [InlineData("""<pc id="1" disp="x">x</pc>""", "Unsupported attribute 'disp' on <pc>")]
    [InlineData("""<sc id="1" dispStart="x"/><ec startRef="1"/>""", "Unsupported attribute 'dispStart' on <sc>")]
    [InlineData("""<ec isolated="yes"/>""", "missing the required 'id' attribute")]
    [InlineData("""<ph id="1" canReorder="sometimes"/>""", "Invalid canReorder value 'sometimes'")]
    [InlineData("""<mrk id="1" translate="maybe">x</mrk>""", "Invalid translate value 'maybe'")]
    [InlineData("""<sm id="1"/><em startRef="1" type="term"/>""", "Unsupported attribute 'type' on <em>")]
    [InlineData("""<cp hex="41">x</cp>""", "<cp> in unit 'u1' must be empty")]
    [InlineData("""<cp hex="zz"/>""", """Invalid <cp hex="zz">""")]
    [InlineData("""<cp hex="110000"/>""", """Invalid <cp hex="110000">""")]
    public async Task Read_RejectsInvalidInlineMarkup(string sourceContent, string expectedMessage)
    {
        var exception = await Assert.ThrowsAsync<XliffFormatException>(() => ReadSourceAsync(sourceContent));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Reads a unit with a single segment and returns that segment's source.</summary>
    private async Task<InlineContent> ReadSourceAsync(string sourceContent) =>
        (await ReadUnitAsync($"<segment><source>{sourceContent}</source></segment>")).Segments.Single().Source;

    private async Task<List<InlineCode>> ReadCodesAsync(string sourceContent) =>
        (await ReadSourceAsync(sourceContent)).OfType<InlineCode>().ToList();

    private async Task<List<InlineMarker>> ReadMarkersAsync(string sourceContent) =>
        (await ReadSourceAsync(sourceContent)).OfType<InlineMarker>().ToList();
}
