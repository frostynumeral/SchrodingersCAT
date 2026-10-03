using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class XliffMatchesTests
{
    [Fact]
    public async Task ReadsTheFilesMatches_WithScoreOriginAndCodes()
    {
        var unit = await UnitAsync("""
            <mtc:matches>
              <mtc:match ref="#s1" matchQuality="85.5" origin="Client TM" type="tm">
                <originalData><data id="d1">&lt;b&gt;</data><data id="d2">&lt;/b&gt;</data></originalData>
                <source>Press <pc id="1" dataRefStart="d1" dataRefEnd="d2">OK</pc> now</source>
                <target>Nyomd meg most: <pc id="1" dataRefStart="d1" dataRefEnd="d2">OK</pc></target>
              </mtc:match>
              <mtc:match ref="#m1" similarity="70" type="mt">
                <source>Press</source>
                <target>Nyomd</target>
              </mtc:match>
            </mtc:matches>
            """);

        var matches = XliffMatches.Read(unit);

        Assert.Equal([("s1", 86, "Client TM"), (null, 70, null)], matches.Select(m => (m.SegmentId, m.Match.Score, m.Match.Origin)));
        var first = matches[0].Match.Entry;
        Assert.Equal("Press OK now", first.Source.ToPlainText());
        Assert.Equal(
            [new InlineCode("1", InlineCodeKind.Opening) { DataRef = "d1", CanOverlap = false }, new InlineCode("1", InlineCodeKind.Closing) { DataRef = "d2", CanOverlap = false }],
            first.Target.OfType<InlineCode>());
    }

    // Someone else's suggestions: a match that breaks the rules is left out, not the unit.
    [Fact]
    public async Task AMatchThatBreaksTheRules_IsLeftOut()
    {
        var unit = await UnitAsync("""
            <mtc:matches>
              <mtc:match ref="#s1"><source>A <ph id="1" dataRef="missing"/></source><target>B</target></mtc:match>
              <mtc:match ref="#s1"><source>A <sc id="1"/></source><target>B</target></mtc:match>
              <mtc:match ref="#s1"><source>A</source></mtc:match>
              <mtc:match ref="#s1" matchQuality="lots"><source>A</source><target>B</target></mtc:match>
            </mtc:matches>
            """);

        var match = Assert.Single(XliffMatches.Read(unit));
        Assert.Equal(0, match.Match.Score); // an unreadable score counts as unknown
    }

    [Fact]
    public async Task AUnitWithoutMatches_HasNone() =>
        Assert.Empty(XliffMatches.Read(await UnitAsync("")));

    private static async Task<TranslationUnit> UnitAsync(string extensions)
    {
        var xml = $"""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:mtc="urn:oasis:names:tc:xliff:matches:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1"><unit id="u1">{extensions}<segment id="s1"><source>Press OK.</source></segment></unit></file>
            </xliff>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return (await new Xliff20Serializer().ReadAsync(stream)).Units.Single();
    }
}
