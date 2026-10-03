using System.Text;
using CsCheck;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Memory.Tests;

public class TmxTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportAndImport_KeepTextCodesAndMetadata_AndTieTheCodesTogether()
    {
        var bold = new InlineCode("b", InlineCodeKind.Opening) { SubType = "xlf:b" };
        var boldEnd = new InlineCode("b", InlineCodeKind.Closing) { SubType = "xlf:b" };
        var placeholder = new InlineCode("ph1", InlineCodeKind.Standalone) { Disp = "{0}" };
        var entry = new TmEntry(
            new InlineContent([new InlineText("Press "), bold, new InlineText("OK"), boldEnd, new InlineText(" for "), placeholder]),
            new InlineContent([placeholder, new InlineText(": nyomd meg: "), bold, new InlineText("OK"), boldEnd]))
        {
            PreviousSource = "Before.",
            NextSource = "After.",
            Document = "strings.xlf",
            Changed = Monday,
            ChangedBy = "translator",
        };

        var (xml, imported) = await RoundTripAsync([entry]);

        Assert.Contains("<seg>Press <bpt i=\"1\" x=\"1\" />OK<ept i=\"1\" /> for <ph x=\"2\" /></seg>", xml, StringComparison.Ordinal);
        Assert.Contains("<prop type=\"x-context-pre\">Before.</prop>", xml, StringComparison.Ordinal);
        Assert.Empty(imported.Skipped);
        var result = Assert.Single(imported.Entries);

        // The code ids come from x, so the source's codes and the target's are the same codes.
        var b = new InlineCode("1", InlineCodeKind.Opening);
        var bEnd = new InlineCode("1", InlineCodeKind.Closing);
        var ph = new InlineCode("2", InlineCodeKind.Standalone);
        Assert.Equal(new InlineContent([new InlineText("Press "), b, new InlineText("OK"), bEnd, new InlineText(" for "), ph]), result.Source);
        Assert.Equal(new InlineContent([ph, new InlineText(": nyomd meg: "), b, new InlineText("OK"), bEnd]), result.Target);
        Assert.Equal(("Before.", "After.", "strings.xlf", Monday, "translator"), (result.PreviousSource, result.NextSource, result.Document, result.Changed, result.ChangedBy));
    }

    [Fact]
    public async Task HalvesWhosePartnerIsInAnotherSegment_AreIt()
    {
        var entry = new TmEntry(
            new InlineContent([new InlineCode("1", InlineCodeKind.Closing), new InlineText("a"), new InlineCode("1", InlineCodeKind.Opening)]),
            InlineContent.FromText("b"));

        var (xml, imported) = await RoundTripAsync([entry]);

        Assert.Contains("<seg><it pos=\"end\" x=\"1\" />a<it pos=\"begin\" x=\"1\" /></seg>", xml, StringComparison.Ordinal);
        Assert.Equal([InlineCodeKind.Closing, InlineCodeKind.Opening], imported.Entries.Single().Source.OfType<InlineCode>().Select(c => c.Kind));
    }

    // Regression (found by the property test): i was reused once a pair closed, so overlapping pairs got two <bpt i="2">.
    [Fact]
    public async Task OverlappingPairs_GetDistinctI()
    {
        InlineCode Half(string id, InlineCodeKind kind) => new(id, kind);
        var entry = new TmEntry(
            new InlineContent([Half("a", InlineCodeKind.Opening), Half("b", InlineCodeKind.Opening), Half("a", InlineCodeKind.Closing),
                Half("c", InlineCodeKind.Opening), Half("b", InlineCodeKind.Closing), Half("c", InlineCodeKind.Closing)]),
            InlineContent.FromText("x"));

        var (xml, imported) = await RoundTripAsync([entry]);

        Assert.Contains("<seg><bpt i=\"1\" x=\"1\" /><bpt i=\"2\" x=\"2\" /><ept i=\"1\" /><bpt i=\"3\" x=\"3\" /><ept i=\"2\" /><ept i=\"3\" /></seg>", xml, StringComparison.Ordinal);
        Assert.Empty(imported.Skipped);
        Assert.Single(imported.Entries);
    }

    [Fact]
    public async Task Import_TakesTheLanguagePair_AndReportsWhatItSkips()
    {
        var tmx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <tmx version="1.4">
              <header creationtool="Other" creationtoolversion="9" segtype="sentence" o-tmf="x" adminlang="en-US" srclang="*all*" datatype="plaintext"/>
              <body>
                <tu creationdate="20250101T120000Z" creationid="someone">
                  <tuv xml:lang="EN-US"><seg>Open the <hi type="b">file</hi>.<ut>{\b}</ut></seg></tuv>
                  <tuv xml:lang="fr-FR"><seg>Ouvrez le fichier.</seg></tuv>
                  <tuv xml:lang="de-AT"><seg>Öffne die Datei.</seg></tuv>
                </tu>
                <tu><tuv xml:lang="en"><seg>Only English.</seg></tuv></tu>
                <tu><tuv xml:lang="en"><seg>Bad <foo/></seg></tuv><tuv xml:lang="de"><seg>Schlecht</seg></tuv></tu>
                <tu><tuv xml:lang="en"><seg>Bad <ept i="1"/></seg></tuv><tuv xml:lang="de"><seg>Schlecht</seg></tuv></tu>
              </body>
            </tmx>
            """;

        var imported = await Tmx.ReadAsync(Stream(tmx), "en", "de");

        var entry = Assert.Single(imported.Entries);
        Assert.Equal(("Open the file.", "Öffne die Datei."), (entry.Source.ToPlainText(), entry.Target.ToPlainText()));
        Assert.Equal((new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), "someone"), (entry.Changed, entry.ChangedBy));
        Assert.Equal([
            "Translation unit 2: no de text.",
            "Translation unit 3: <foo> isn't allowed in a <seg>.",
            "Translation unit 4: <ept i=\"1\"> has no <bpt> before it.",
        ], imported.Skipped);
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE tmx [<!ENTITY a "aaaa">]><tmx version="1.4"><body/></tmx>""", "DTD")]
    [InlineData("""<xliff version="2.0"/>""", "isn't TMX")]
    [InlineData("""<tmx version="1.4"><body>""", "well-formed")]
    public async Task Import_RefusesWhatIsntSafeTmx(string content, string reason)
    {
        var error = await Assert.ThrowsAsync<TmFormatException>(() => Tmx.ReadAsync(Stream(content), "en", "de"));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_StopsAtTheSizeLimit()
    {
        var content = "<tmx version=\"1.4\"><body>" + string.Concat(Enumerable.Repeat("<tu/>", 10_000)) + "</body></tmx>";

        var error = await Assert.ThrowsAsync<TmFormatException>(() => Tmx.ReadAsync(Stream(content), "en", "de", maxInputBytes: 1000));
        Assert.Contains("1,000-byte limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_StopsAtTheNestingLimit()
    {
        var content = "<tmx version=\"1.4\"><body><tu><tuv xml:lang=\"en\"><seg>" + string.Concat(Enumerable.Repeat("<hi>", 5000)) + string.Concat(Enumerable.Repeat("</hi>", 5000)) + "</seg></tuv></tu></body></tmx>";

        var error = await Assert.ThrowsAsync<TmFormatException>(() => Tmx.ReadAsync(Stream(content), "en", "de"));
        Assert.Contains($"more than {Tmx.MaxElementDepth} deep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_SkipsTextXmlCantHold_AndSaysSo()
    {
        var entries = new[]
        {
            new TmEntry(InlineContent.FromText("ok"), InlineContent.FromText("jó")),
            new TmEntry(InlineContent.FromText("bad\uD800"), InlineContent.FromText("rossz")),
        };
        using var stream = new MemoryStream();

        var export = await Tmx.WriteAsync(stream, entries, "en", "hu");

        Assert.Equal(1, export.Written);
        Assert.Equal(["Entry 2: its text has characters XML can't hold."], export.Skipped);
    }

    // Whatever the documents hold, export then import keeps every writable entry's text and code kinds.
    [Fact]
    public void ExportThenImport_KeepsTextAndCodes() =>
        XliffGen.Document.Sample(document =>
        {
            var entries = document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null)
                .Select(s => new TmEntry(s.Source, s.Target!) { Changed = Monday }).ToList();

            var (_, imported) = RoundTripAsync(entries).GetAwaiter().GetResult();

            var writable = entries.Where(e => e.Source.Concat(e.Target).OfType<InlineText>().All(t => IsXml(t.Text))).ToList();
            Assert.Equal(writable.Count, imported.Entries.Count);
            Assert.Empty(imported.Skipped);
            foreach (var (expected, actual) in writable.Zip(imported.Entries))
            {
                Assert.Equal(expected.Source.ToPlainText(), actual.Source.ToPlainText());
                Assert.Equal(expected.Target.ToPlainText(), actual.Target.ToPlainText());
                Assert.Equal(expected.Source.OfType<InlineCode>().Select(c => c.Kind), actual.Source.OfType<InlineCode>().Select(c => c.Kind));
                Assert.Equal(expected.Target.OfType<InlineCode>().Select(c => c.Kind), actual.Target.OfType<InlineCode>().Select(c => c.Kind));
            }
        });

    // TMX files come from anywhere: damaged ones fail only with TmFormatException, or import what they can.
    [Fact]
    public void DamagedTmx_FailsOnlyWithTmFormatException() =>
        Gen.Select(XliffGen.Document, Gen.Int[0, 100_000].Array[1, 6], Gen.OneOfConst("<", ">", "&", "\"", "<tu>", "</seg>", "<!DOCTYPE x>", "\u0000", "<bpt i=\"1\"/>", "")).Sample((document, positions, damage) =>
        {
            var entries = document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null).Select(s => new TmEntry(s.Source, s.Target!));
            using var stream = new MemoryStream();
            Tmx.WriteAsync(stream, entries, "en", "hu").GetAwaiter().GetResult();
            var text = new StringBuilder(Encoding.UTF8.GetString(stream.ToArray()));
            foreach (var position in positions)
            {
                var at = position % (text.Length + 1);
                if (damage.Length == 0)
                    text.Remove(at, Math.Min(7, text.Length - at)); // cut a piece out
                else
                    text.Insert(at, damage);
            }

            try
            {
                Tmx.ReadAsync(Stream(text.ToString()), "en", "hu").GetAwaiter().GetResult();
            }
            catch (TmFormatException)
            {
            }
        });

    private static bool IsXml(string text)
    {
        try
        {
            System.Xml.XmlConvert.VerifyXmlChars(text);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static async Task<(string Xml, TmxImport Imported)> RoundTripAsync(IEnumerable<TmEntry> entries)
    {
        using var stream = new MemoryStream();
        await Tmx.WriteAsync(stream, entries, "en", "hu");
        var xml = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        return (xml, await Tmx.ReadAsync(stream, "en", "hu"));
    }

    private static MemoryStream Stream(string content) => new(Encoding.UTF8.GetBytes(content));
}
