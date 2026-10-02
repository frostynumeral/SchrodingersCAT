using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

/// <summary>Round-trips built from the inline-content examples in the XLIFF 2.0 specification.</summary>
public class Xliff20InlineRoundTripTests : Xliff20SerializerTestBase
{
    public static TheoryData<string, string> SpecExamples => new()
    {
        {
            "pc with originalData",
            """
            <unit id="1">
              <originalData>
                <data id="d1">&lt;B&gt;</data>
                <data id="d2">&lt;/B&gt;</data>
              </originalData>
              <segment>
                <source><pc id="1" dataRefStart="d1" dataRefEnd="d2">Bold</pc> text</source>
                <target><pc id="1" dataRefStart="d1" dataRefEnd="d2">Félkövér</pc> szöveg</target>
              </segment>
            </unit>
            """
        },
        {
            "standalone placeholder",
            """
            <unit id="1">
              <originalData><data id="d1">{0}</data><data id="d2">&lt;br/&gt;</data></originalData>
              <segment state="translated">
                <source>Hello <ph id="1" dataRef="d1" disp="{0}" equiv="name"/>!<ph id="2" dataRef="d2" type="fmt" subType="xlf:lb"/></source>
                <target><ph id="2" dataRef="d2" type="fmt" subType="xlf:lb"/>Szia <ph id="1" dataRef="d1" disp="{0}" equiv="name"/>!</target>
              </segment>
            </unit>
            """
        },
        {
            "pair spanning segments",
            """
            <unit id="1">
              <originalData><data id="d1">&lt;B&gt;</data><data id="d2">&lt;/B&gt;</data></originalData>
              <segment id="s1">
                <source><sc id="1" dataRef="d1"/>First sentence. </source>
                <target><sc id="1" dataRef="d1"/>Első mondat. </target>
              </segment>
              <segment id="s2">
                <source>Second sentence.<ec startRef="1" dataRef="d2"/></source>
                <target>Második mondat.<ec startRef="1" dataRef="d2"/></target>
              </segment>
            </unit>
            """
        },
        {
            "pair well-formed in the source but spanning segments in the target",
            """
            <unit id="1">
              <segment id="s1">
                <source><sc id="1"/>One.<ec startRef="1"/></source>
                <target><sc id="1"/>Egy.</target>
              </segment>
              <segment id="s2">
                <source>Two.</source>
                <target>Kettő.<ec startRef="1"/></target>
              </segment>
            </unit>
            """
        },
        {
            "isolated codes in separate units",
            """
            <unit id="1">
              <originalData><data id="d1">&lt;B&gt;</data></originalData>
              <segment><source><sc id="1" isolated="yes" dataRef="d1"/>First sentence. </source></segment>
            </unit>
            <unit id="2">
              <originalData><data id="d2">&lt;/B&gt;</data></originalData>
              <segment><source>Second sentence.<ec id="1" isolated="yes" dataRef="d2"/></source></segment>
            </unit>
            """
        },
        {
            "overlapping codes",
            """
            <unit id="1">
              <segment><source><sc id="1" type="fmt" subType="xlf:b"/>Bold, <sc id="2" type="fmt" subType="xlf:i"/>both<ec startRef="1" type="fmt" subType="xlf:b"/>, italic<ec startRef="2" type="fmt" subType="xlf:i"/></source></segment>
            </unit>
            """
        },
        {
            "nested annotations",
            """
            <unit id="1">
              <notes><note id="n1">Product name</note></notes>
              <segment>
                <source>Buy <mrk id="m1" translate="no"><mrk id="m2" type="term" ref="#n=n1">ACME</mrk> Rocket</mrk> now, <mrk id="m3" type="comment" value="informal">please</mrk>.</source>
                <target>Vegye meg <mrk id="m1" translate="no"><mrk id="m2" type="term" ref="#n=n1">ACME</mrk> Rocket</mrk> most, <mrk id="m3" type="comment" value="informal">kérem</mrk>.</target>
              </segment>
            </unit>
            """
        },
        {
            "annotation spanning segments and crossing a code",
            """
            <unit id="1">
              <segment id="s1"><source><sm id="m1" translate="no"/>Sentence <pc id="1">A.</pc> </source></segment>
              <segment id="s2"><source>Sentence <pc id="2">B<em startRef="m1"/>.</pc></source></segment>
            </unit>
            """
        },
        {
            "code points invalid in XML",
            """
            <unit id="1">
              <originalData><data id="d1"><cp hex="001B"/>[1m</data></originalData>
              <segment><source>Ctrl+C=<cp hex="0003"/>, escape<ph id="1" dataRef="d1"/>, lone <cp hex="DFFF"/></source></segment>
            </unit>
            """
        },
        {
            "ignorable containing a code",
            """
            <unit id="1">
              <segment id="s1"><source>First.</source></segment>
              <ignorable><source> <ph id="1"/> </source></ignorable>
              <segment id="s2"><source>Second.</source></segment>
            </unit>
            """
        },
    };

    [Theory]
    [MemberData(nameof(SpecExamples))]
    public async Task RoundTrip_SpecExample(string name, string units)
    {
        _ = name; // only names the test case

        var original = await ReadAsync(WrapUnits(units));
        var written = await WriteAsync(original);
        var reread = await ReadAsync(written);

        AssertSameUnits(original, reread);
        Assert.Equal(written, await WriteAsync(reread)); // writing is stable
    }

    [Fact]
    public async Task PcAndScEc_WithTheSameInformation_LoadAndWriteIdentically()
    {
        // canOverlap defaults differ between the two forms, so it has to be explicit for them to be equivalent.
        var pc = await ReadAsync(WrapUnits("""
            <unit id="1">
              <originalData><data id="d1">&lt;B&gt;</data><data id="d2">&lt;/B&gt;</data></originalData>
              <segment><source><pc id="1" type="fmt" dataRefStart="d1" dataRefEnd="d2">Bold</pc></source></segment>
            </unit>
            """));
        var scEc = await ReadAsync(WrapUnits("""
            <unit id="1">
              <originalData><data id="d1">&lt;B&gt;</data><data id="d2">&lt;/B&gt;</data></originalData>
              <segment><source><sc id="1" type="fmt" canOverlap="no" dataRef="d1"/>Bold<ec startRef="1" type="fmt" canOverlap="no" dataRef="d2"/></source></segment>
            </unit>
            """));

        AssertSameUnits(pc, scEc);
        Assert.Equal(await WriteAsync(pc), await WriteAsync(scEc));
    }

    private static void AssertSameUnits(TranslationDocument expected, TranslationDocument actual)
    {
        Assert.Equal(expected.Units.Count(), actual.Units.Count());
        foreach (var (e, a) in expected.Units.Zip(actual.Units))
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.OriginalData, a.OriginalData);
            Assert.Equal(e.Segments.Select(s => (s.Id, s.IsIgnorable, s.State)), a.Segments.Select(s => (s.Id, s.IsIgnorable, s.State)));
            Assert.Equal(e.Segments.Select(s => s.Source), a.Segments.Select(s => s.Source));
            Assert.Equal(e.Segments.Select(s => s.Target), a.Segments.Select(s => s.Target));
        }
    }
}
