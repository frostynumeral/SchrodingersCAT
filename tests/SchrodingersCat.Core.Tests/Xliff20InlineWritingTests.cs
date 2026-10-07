using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

public class Xliff20InlineWritingTests : Xliff20SerializerTestBase
{
    [Fact]
    public async Task Write_WellFormedPairAndOriginalData()
    {
        var xml = await RoundTripAsync("""
            <originalData><data id="d1">&lt;b&gt;</data><data id="d2">&lt;/b&gt;</data></originalData>
            <segment><source>a <pc id="1" type="fmt" dataRefStart="d1" dataRefEnd="d2">b</pc></source></segment>
            """);

        Assert.Contains("""<data id="d1">&lt;b&gt;</data>""", xml);
        Assert.Contains("""<source>a <pc id="1" type="fmt" dataRefStart="d1" dataRefEnd="d2">b</pc></source>""", xml);
    }

    [Fact]
    public async Task Write_NormalisesScEcInOneSegmentToPc_KeepingCanOverlap()
    {
        var xml = await RoundTripAsync("""<segment><source>a<sc id="1"/>b<ec startRef="1"/></source></segment>""");

        // <sc>/<ec> default to canOverlap="yes", <pc> to "no", so the resolved value has to be written.
        Assert.Contains("""<source>a<pc id="1" canOverlap="yes">b</pc></source>""", xml);
    }

    [Fact]
    public async Task Write_OverlappingPairs_FallBackToScEc()
    {
        var xml = await RoundTripAsync("""
            <segment><source><sc id="1"/>a<sc id="2"/>b<ec startRef="1"/>c<ec startRef="2"/></source></segment>
            """);

        Assert.Contains("""<source><sc id="1" />a<pc id="2" canOverlap="yes">b<ec startRef="1" />c</pc></source>""", xml);
    }

    [Fact]
    public async Task Write_PairSpanningSegments_UsesScEc()
    {
        var xml = await RoundTripAsync("""
            <segment id="s1"><source>a<sc id="1"/>b</source><target>A<sc id="1"/>B</target></segment>
            <ignorable><source> </source></ignorable>
            <segment id="s2"><source>c<ec startRef="1"/>d</source><target>C<ec startRef="1"/>D</target></segment>
            """);

        Assert.Contains("""<source>a<sc id="1" />b</source>""", xml);
        Assert.Contains("""<target>C<ec startRef="1" />D</target>""", xml);
    }

    [Fact]
    public async Task Write_IsolatedCodes()
    {
        var xml = await RoundTripAsync("""
            <segment><source><sc id="1" isolated="yes"/>a<ec id="2" isolated="yes" dir="rtl"/></source></segment>
            """);

        Assert.Contains("""<source><sc id="1" isolated="yes" />a<ec id="2" isolated="yes" dir="rtl" /></source>""", xml);
    }

    [Fact]
    public async Task Write_PairWithDifferentHalves_StaysScEc()
    {
        var xml = await RoundTripAsync("""
            <segment><source><sc id="1" type="fmt"/>a<ec startRef="1" type="ui"/></source></segment>
            """);

        Assert.Contains("""<source><sc id="1" type="fmt" />a<ec startRef="1" type="ui" /></source>""", xml);
    }

    [Fact]
    public async Task Write_Markers()
    {
        var xml = await RoundTripAsync("""
            <segment id="s1"><source><mrk id="m1" translate="no" type="term" ref="#n1" value="v"><pc id="1">ACME</pc></mrk> is <sm id="m2" type="comment"/>great.</source></segment>
            <segment id="s2"><source>Really<em startRef="m2"/>.</source></segment>
            """);

        Assert.Contains("""<source><mrk id="m1" translate="no" type="term" ref="#n1" value="v"><pc id="1">ACME</pc></mrk> is <sm id="m2" type="comment" />great.</source>""", xml);
        Assert.Contains("""<source>Really<em startRef="m2" />.</source>""", xml);
    }

    [Fact]
    public async Task Write_MarkerCrossingACode_FallsBackToSmEm()
    {
        var xml = await RoundTripAsync("""<segment><source><sm id="m1"/>a<pc id="1">b<em startRef="m1"/>c</pc></source></segment>""");

        Assert.Contains("""<source><sm id="m1" />a<pc id="1">b<em startRef="m1" />c</pc></source>""", xml);
    }

    [Fact]
    public async Task Write_EncodesCharactersInvalidInXmlAsCp()
    {
        var xml = await RoundTripAsync("""
            <originalData><data id="d1"><cp hex="1B"/>[1m</data></originalData>
            <segment><source>a<cp hex="1"/>b<ph id="1" dataRef="d1"/><cp hex="D800"/></source></segment>
            """);

        Assert.Contains("""<data id="d1"><cp hex="001B" />[1m</data>""", xml);
        Assert.Contains("""<source>a<cp hex="0001" />b<ph id="1" dataRef="d1" /><cp hex="D800" /></source>""", xml);
    }

    [Fact]
    public async Task Write_ContentStartingWithAnElement_IsNotIndented()
    {
        var xml = await RoundTripAsync("""<segment><source><pc id="1"><ph id="2"/></pc></source></segment>""");

        Assert.Contains("""<source><pc id="1"><ph id="2" /></pc></source>""", xml);
    }

    [Fact]
    public async Task Write_KeepsExtensionAttributes_DeclaringTheNamespaceOnce()
    {
        var xml = await RoundTripAsync("""
            <segment><source><ph id="1" fs:fs="br"/><pc id="2" fs:fs="b">x</pc></source></segment>
            """);

        Assert.Contains("""<source><ph id="1" fs:fs="br" /><pc id="2" fs:fs="b">x</pc></source>""", xml);
        Assert.Single(xml.Split($"xmlns:fs=\"{FsNamespace}\"")[1..]);
    }

    [Fact]
    public async Task Write_RoundTripsAllCodeAttributes()
    {
        await RoundTripAsync("""
            <originalData><data id="d1">a</data><data id="d2">b</data><data id="d3"> c </data></originalData>
            <segment id="s1">
              <source><pc id="1" type="fmt" subType="xlf:b" copyOf="9" canCopy="no" canDelete="no" canReorder="firstNo" canOverlap="yes" dir="rtl" dataRefStart="d1" dataRefEnd="d2" dispStart="[" dispEnd="]" equivStart="(" equivEnd=")" subFlowsStart="u2" subFlowsEnd="u3">x</pc><ph id="2" type="ui" subType="x:y" canCopy="no" canDelete="no" canReorder="no" dataRef="d3" disp="{0}" equiv="" subFlows="u4"/><sc id="3" canOverlap="no" dir="ltr" disp="&lt;"/></source>
              <target><ph id="2" copyOf="2"/></target>
            </segment>
            <segment id="s2"><source>y<ec startRef="3" canOverlap="no" equiv="&gt;"/></source><target/></segment>
            """);
    }

    [Fact]
    public async Task Write_CodesWithoutCanOverlap_WritesNoAttribute()
    {
        var xml = await WriteAsync(DocumentWith(
            new InlineCode("1", InlineCodeKind.Opening), new InlineText("b"), new InlineCode("1", InlineCodeKind.Closing)));

        Assert.Contains("""<source><pc id="1">b</pc></source>""", xml);
    }

    [Fact]
    public async Task Write_TreatsCodesAsCharactersForWhitespacePreservation()
    {
        var xml = await RoundTripAsync("""
            <segment><source>Click <ph id="1"/> here</source></segment>
            <segment><source xml:space="preserve">Click <ph id="2"/>  here</source></segment>
            """);

        Assert.Contains("""<source>Click <ph id="1" /> here</source>""", xml);
        Assert.Contains("""<source xml:space="preserve">Click <ph id="2" />  here</source>""", xml);
    }

    public static TheoryData<InlineElement[], string> InconsistentContent => new()
    {
        { [new InlineCode("1", InlineCodeKind.Opening), new InlineText("a")], "the opening half of code '1' has no closing half" },
        { [new InlineText("a"), new InlineCode("1", InlineCodeKind.Closing)], "the closing half of code '1' has no opening half before it" },
        { [new InlineCode("1", InlineCodeKind.Closing), new InlineCode("1", InlineCodeKind.Opening)], "the closing half of code '1' has no opening half before it" },
        { [new InlineMarker("m1", InlineMarkerKind.Opening)], "the opening half of marker 'm1' has no closing half" },
        { [new InlineCode("1", InlineCodeKind.Opening), new InlineMarker("1", InlineMarkerKind.Closing)], "the closing half of marker '1' has no opening half before it" },
        { [new InlineCode("1", InlineCodeKind.Standalone), new InlineCode("1", InlineCodeKind.Standalone)], "the id '1' is used more than once" },
        { [new InlineCode("1", InlineCodeKind.Opening) { IsIsolated = true }, new InlineCode("1", InlineCodeKind.Closing)], "the closing half of code '1' has no opening half before it" },
        { [new InlineCode("s1", InlineCodeKind.Standalone)], "the id 's1' is used more than once" },
        { [new InlineCode("1", InlineCodeKind.Standalone) { DataRef = "missing" }], "dataRef 'missing' does not match any key in OriginalData" },
        { [new InlineCode("1", (InlineCodeKind)42)], "code '1' has an undefined Kind or CanReorder" },
        { [new InlineCode("1", InlineCodeKind.Standalone) { CanReorder = (CanReorder)42 }], "code '1' has an undefined Kind or CanReorder" },
        { [new InlineMarker("m1", (InlineMarkerKind)42)], "marker 'm1' has an undefined Kind" },
    };

    [Theory]
    [MemberData(nameof(InconsistentContent))]
    public async Task Write_RejectsContentTheReaderWouldReject(InlineElement[] content, string expectedMessage)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(DocumentWith(content)));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<InlineElement[], string> UnrepresentableContent => new()
    {
        { [new InlineCode("1", InlineCodeKind.Standalone) { Dir = "rtl" }], "Standalone code '1' has Dir, CanOverlap or IsIsolated set" },
        { [new InlineCode("1", InlineCodeKind.Standalone) { CanOverlap = true }], "Standalone code '1' has Dir, CanOverlap or IsIsolated set" },
        { [new InlineCode("1", InlineCodeKind.Standalone) { IsIsolated = true }], "Standalone code '1' has Dir, CanOverlap or IsIsolated set" },
        { [new InlineCode("1", InlineCodeKind.Opening), new InlineCode("1", InlineCodeKind.Closing) { Dir = "rtl" }], "Closing code '1' has Dir set" },
        { [new InlineMarker("m1", InlineMarkerKind.Opening), new InlineMarker("m1", InlineMarkerKind.Closing) { Type = "term" }], "The closing half of marker 'm1' has attributes set" },
        {
            [new InlineMarker("m1", InlineMarkerKind.Opening), new InlineMarker("m1", InlineMarkerKind.Closing) { ExtensionAttributes = [new ExtensionAttr(FsNamespace, "fs", "x")] }],
            "The closing half of marker 'm1' has attributes set"
        },
        { [new InlineCode("1", InlineCodeKind.Standalone) { ExtensionAttributes = [new ExtensionAttr("", "foo", "x")] }], "Extension attribute 'foo' must be in a namespace other than" },
        {
            [new InlineCode("1", InlineCodeKind.Standalone) { ExtensionAttributes = [new ExtensionAttr("urn:oasis:names:tc:xliff:document:2.0", "foo", "x")] }],
            "Extension attribute 'foo' must be in a namespace other than"
        },
        { [new InlineCode("1", InlineCodeKind.Standalone) { ExtensionAttributes = [new ExtensionAttr(FsNamespace, "1fs", "x")] }], "Extension attribute name '1fs' is not a valid XML name" },
        { [new InlineCode("1", InlineCodeKind.Standalone) { Disp = "\u0001" }], "the disp attribute of <ph> in unit 'u1': it contains U+0001" },
        { [new InlineMarker("m1", InlineMarkerKind.Opening) { Value = "\uFFFF" }, new InlineMarker("m1", InlineMarkerKind.Closing)], "the value attribute of <mrk> in unit 'u1': it contains U+FFFF" },
    };

    [Theory]
    [MemberData(nameof(UnrepresentableContent))]
    public async Task Write_RejectsContentXliffCannotRepresent(InlineElement[] content, string expectedMessage)
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => WriteAsync(DocumentWith(content)));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Reads, writes and reads again; asserts nothing changed and returns the written XML.</summary>
    private async Task<string> RoundTripAsync(string unitContent)
    {
        var original = await ReadAsync(WrapUnitContent(unitContent));
        var xml = await WriteAsync(original);
        var unit = (await ReadAsync(xml)).Units.Single();
        var expected = original.Units.Single();

        Assert.Equal(expected.OriginalData, unit.OriginalData);
        Assert.Equal(expected.Segments.Select(s => s.Source), unit.Segments.Select(s => s.Source));
        Assert.Equal(expected.Segments.Select(s => s.Target), unit.Segments.Select(s => s.Target));
        return xml;
    }
}
