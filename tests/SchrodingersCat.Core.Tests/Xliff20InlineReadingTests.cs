using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

public class Xliff20InlineReadingTests
{
    private const string FsNamespace = "urn:oasis:names:tc:xliff:fs:2.0";

    private readonly Xliff20Serializer _serializer = new();

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

        var bold = new InlineCode("2", InlineCodeKind.Opening) { Type = "fmt", SubType = "xlf:b", CanOverlap = false };
        Assert.Equal(new InlineContent(
        [
            new InlineText("Line"),
            new InlineCode("1", InlineCodeKind.Standalone) { DataRef = "d1", Equiv = " " },
            bold with { DataRef = "d2" },
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
        var pc = await ReadUnitAsync("""<segment><source><pc id="1" type="fmt" canOverlap="yes">x</pc></source></segment>""");
        var scEc = await ReadUnitAsync("""<segment><source><sc id="1" type="fmt"/>x<ec startRef="1" type="fmt"/></source></segment>""");

        Assert.Equal(pc.Segments[0].Source, scEc.Segments[0].Source);
    }

    [Fact]
    public async Task Read_ResolvesCanOverlapToTheElementDefault()
    {
        var pc = await ReadUnitAsync("""<segment><source><pc id="1">x</pc></source></segment>""");
        var scEc = await ReadUnitAsync("""<segment><source><sc id="1"/>x<ec startRef="1"/></source></segment>""");

        Assert.All(pc.Segments[0].Source.OfType<InlineCode>(), c => Assert.False(c.CanOverlap));
        Assert.All(scEc.Segments[0].Source.OfType<InlineCode>(), c => Assert.True(c.CanOverlap));
    }

    [Fact]
    public async Task Read_PcStartingWithFirstNoEndsWithNo()
    {
        var unit = await ReadUnitAsync("""<segment><source><pc id="1" canReorder="firstNo">x</pc></source></segment>""");

        var codes = unit.Segments[0].Source.OfType<InlineCode>().ToList();
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
        var unit = await ReadUnitAsync("""<segment><source><sc id="1" isolated="yes"/>a<ec id="2" isolated="yes" dir="rtl"/></source></segment>""");

        var codes = unit.Segments[0].Source.OfType<InlineCode>().ToList();
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
        var unit = await ReadUnitAsync("""<segment><source><ph id="1" fs:fs="br"/><pc id="2" fs:fs="b">x</pc></source></segment>""");

        var codes = unit.Segments[0].Source.OfType<InlineCode>().ToList();
        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "br")], codes[0].ExtensionAttributes);
        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "b")], codes[1].ExtensionAttributes);
    }

    [Theory]
    [InlineData("""<segment><source><ph id="1"/><ph id="1"/></source></segment>""")]
    [InlineData("""<segment id="1"><source><ph id="1"/></source></segment>""")]
    [InlineData("""<segment><source><ph id="1"/></source></segment><ignorable><source><ph id="1"/></source></ignorable>""")]
    [InlineData("""<segment id="s"><source>a</source></segment><segment id="s"><source>b</source></segment>""")]
    [InlineData("""<segment><source><ph id="1" dataRef="d1"/></source></segment>""")]
    [InlineData("""<originalData><data id="d1">a</data><data id="d1">b</data></originalData><segment><source>x</source></segment>""")]
    [InlineData("""<segment><source><sc id="1"/></source></segment>""")]
    [InlineData("""<segment><source><ec startRef="1"/></source></segment>""")]
    [InlineData("""<segment><source><sc id="1" isolated="yes"/><ec startRef="1"/></source></segment>""")]
    [InlineData("""<segment><source><sc id="1"/><ec startRef="1" dir="rtl"/></source></segment>""")]
    [InlineData("""<segment><source><sm id="1"/></source></segment>""")]
    [InlineData("""<segment><source><em startRef="1"/></source></segment>""")]
    [InlineData("""<segment><source><ph/></source></segment>""")]
    [InlineData("""<segment><source><ph id="1">x</ph></source></segment>""")]
    [InlineData("""<segment><source><ph id="1" foo="x"/></source></segment>""")]
    [InlineData("""<segment><source><ph id="1" canCopy="maybe"/></source></segment>""")]
    [InlineData("""<segment><source><b>x</b></source></segment>""")]
    [InlineData("""<segment><source><cp hex="zz"/></source></segment>""")]
    [InlineData("""<segment><source><cp hex="110000"/></source></segment>""")]
    public async Task Read_RejectsInvalidInlineContent(string content)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadUnitAsync(content));
    }

    [Fact]
    public async Task Write_RejectsOriginalDataInsteadOfDroppingIt()
    {
        var doc = await ReadAsync(Wrap("""<originalData><data id="d1">x</data></originalData><segment><source>a</source></segment>"""));

        await Assert.ThrowsAsync<NotSupportedException>(() => _serializer.WriteAsync(doc, Stream.Null));
    }

    [Fact]
    public async Task Write_RejectsCharactersInvalidInXmlInsteadOfFailingInXmlWriter()
    {
        var doc = await ReadAsync(Wrap("""<segment><source>a<cp hex="1"/></source></segment>"""));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => _serializer.WriteAsync(doc, Stream.Null));
        Assert.Contains("<cp>", ex.Message);
    }

    private static string Wrap(string unitContent) => $"""
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:fs="{FsNamespace}" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1"><unit id="u1">{unitContent}</unit></file>
        </xliff>
        """;

    private async Task<TranslationUnit> ReadUnitAsync(string unitContent) =>
        (await ReadAsync(Wrap(unitContent))).Units.Single();

    private async Task<TranslationDocument> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await _serializer.ReadAsync(stream);
    }
}
