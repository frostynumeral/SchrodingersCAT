using System.Diagnostics;
using System.Text;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class TbxTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the samples

    [Fact]
    public async Task Tbx2Sample_KeepsTermsStatusNotesAndTheLastModification_AndReportsTheRest()
    {
        var imported = await ReadSampleAsync("tbx2-basic.tbx");

        Assert.Equal(
        [
            new TermEntry(
                [new Term("folder", TermStatus.Preferred), new Term("directory", TermStatus.Admitted) { Note = "In command-line contexts." }],
                [new Term("mappa", TermStatus.Preferred), new Term("könyvtár", TermStatus.Forbidden) { Note = "Only for libraries." }],
                "A container for files.\nUse in UI strings.\nen: US spelling.",
                new DateTimeOffset(2025, 3, 14, 9, 26, 53, TimeSpan.Zero),
                "bela"),
            new TermEntry([new Term("file")], [new Term("fájl")], null, default, null),
            new TermEntry([new Term("SchrodingersCAT", TermStatus.Preferred)], [], null, default, null),
            new TermEntry([new Term("save", TermStatus.Preferred)], [new Term("mentés")], null, default, null),
        ], imported.Entries);
        Assert.Equal(["Entry 3: no en term.", "Entry 5: no hu term."], imported.Skipped);
        Assert.Equal(["Entry 2: the status \"obsoleteTerm-admn-sts\" of \"fájl\" isn't known; the term is imported without one."], imported.Notices);
        Assert.Equal(new Dictionary<string, int>
        {
            ["langSet xml:lang=\"de\""] = 1,
            ["partOfSpeech"] = 1,
            ["source"] = 1,
            ["subjectField"] = 1,
        }, imported.Dropped);
    }

    [Fact]
    public async Task Utf16Sample_IsRead()
    {
        var imported = await ReadSampleAsync("tbx2-utf16.tbx");

        Assert.Equal(
        [
            new TermEntry([new Term("guard")], [new Term("őr", TermStatus.Preferred)], null, default, null),
            new TermEntry([new Term("emoji 😀")], [new Term("emodzsi 😀")], null, default, null),
        ], imported.Entries);
    }

    [Fact]
    public async Task Latin1Sample_IsRead()
    {
        var imported = await ReadSampleAsync("tbx2-latin1.tbx");

        var entry = Assert.Single(imported.Entries);
        Assert.Equal([new Term("kávé"), new Term("feketekávé", TermStatus.Admitted) { Note = "Black, without milk; spelt 'fekete kávé' too." }], entry.Target);
    }

    [Fact]
    public async Task Tbx3DcaSample_IsRead()
    {
        var imported = await ReadSampleAsync("tbx3-basic-dca.tbx");

        Assert.Equal(
        [
            new TermEntry(
                [new Term("folder", TermStatus.Preferred), new Term("directory", TermStatus.Forbidden)],
                [new Term("mappa", TermStatus.Admitted) { Note = "Informal." }],
                "A container for files.\nhu: Fájlok tárolója.",
                new DateTimeOffset(2025, 3, 14, 9, 26, 53, 500, TimeSpan.Zero),
                "bela"),
            new TermEntry([new Term("colour")], [new Term("szín")], null, default, null),
        ], imported.Entries);
        Assert.Empty(imported.Skipped);
        Assert.Empty(imported.Notices);
        Assert.Equal(new Dictionary<string, int> { ["partOfSpeech"] = 1, ["xref"] = 1 }, imported.Dropped);
    }

    // DCT elements are matched by namespace URI: x and y are Min and Basic here, and min is another namespace.
    [Fact]
    public async Task Tbx3DctSample_MatchesByNamespaceNotPrefix()
    {
        var imported = await ReadSampleAsync("tbx3-basic-dct.tbx");

        Assert.Equal(
        [
            new TermEntry(
                [new Term("folder", TermStatus.Preferred), new Term("directory")],
                [new Term("mappa", TermStatus.Admitted) { Note = "Informal." }],
                "A container for files.",
                new DateTimeOffset(2024, 5, 1, 8, 0, 0, TimeSpan.Zero),
                "anna"),
        ], imported.Entries);
        Assert.Equal(new Dictionary<string, int> { ["administrativeStatus"] = 1, ["partOfSpeech"] = 1, ["subjectField"] = 1 }, imported.Dropped);
    }

    // ---------------------------------------------------------------- import rules

    [Theory]
    [InlineData("", TermStatus.Unspecified)]
    [InlineData("<termNote type=\"administrativeStatus\">preferredTerm-admn-sts</termNote>", TermStatus.Preferred)]
    [InlineData("<termNote type=\"administrativeStatus\">admittedTerm-admn-sts</termNote>", TermStatus.Admitted)]
    [InlineData("<termNote type=\"administrativeStatus\">deprecatedTerm-admn-sts</termNote>", TermStatus.Forbidden)]
    [InlineData("<termNote type=\"administrativeStatus\">supersededTerm-admn-sts</termNote>", TermStatus.Forbidden)]
    [InlineData("<termNote type=\"normativeAuthorization\">preferredTerm</termNote>", TermStatus.Preferred)]
    [InlineData("<termNote type=\"normativeAuthorization\">standardizedTerm</termNote>", TermStatus.Preferred)]
    [InlineData("<termNote type=\"normativeAuthorization\">legalTerm</termNote>", TermStatus.Preferred)]
    [InlineData("<termNote type=\"normativeAuthorization\">regulatedTerm</termNote>", TermStatus.Preferred)]
    [InlineData("<termNote type=\"normativeAuthorization\">admittedTerm</termNote>", TermStatus.Admitted)]
    [InlineData("<termNote type=\"normativeAuthorization\">deprecatedTerm</termNote>", TermStatus.Forbidden)]
    [InlineData("<termNote type=\"normativeAuthorization\">supersededTerm</termNote>", TermStatus.Forbidden)]
    [InlineData("<termNote type=\"normativeAuthorization\">admittedTerm</termNote><termNote type=\"administrativeStatus\">deprecatedTerm-admn-sts</termNote>", TermStatus.Forbidden)]
    [InlineData("<termNote type=\"administrativeStatus\"> preferredTerm-admn-sts </termNote>", TermStatus.Preferred)]
    public async Task Status_FollowsTheTable(string termNotes, TermStatus expected)
    {
        var imported = await ReadAsync(Tbx2($"<langSet xml:lang=\"en\"><tig><term>a</term>{termNotes}</tig></langSet>"));

        Assert.Equal(expected, imported.Entries.Single().Source.Single().Status);
        Assert.Empty(imported.Notices);
    }

    [Fact]
    public async Task UnknownStatus_IsUnspecified_AndReported()
    {
        var imported = await ReadAsync(Tbx2("<langSet xml:lang=\"en\"><tig><term>a</term><termNote type=\"administrativeStatus\">preferred</termNote></tig></langSet>"));

        Assert.Equal(TermStatus.Unspecified, imported.Entries.Single().Source.Single().Status);
        Assert.Equal(["Entry 1: the status \"preferred\" of \"a\" isn't known; the term is imported without one."], imported.Notices);
    }

    [Fact]
    public async Task Notes_AreJoinedInDocumentOrder_EachLanguagesPrefixed_AndOtherLanguagesDropped()
    {
        var imported = await ReadAsync(Tbx2("""
            <langSet xml:lang="de"><note>Deutsch.</note><tig><term>Ordner</term></tig></langSet>
            <langSet xml:lang="hu"><descrip type="definition">Tároló.</descrip><tig><term>mappa</term><note>one</note><note>two</note></tig></langSet>
            <note>Concept.</note>
            <langSet xml:lang="en"><note></note><tig><term>folder</term><note></note></tig></langSet>
            """));

        var entry = imported.Entries.Single();
        Assert.Equal("hu: Tároló.\nConcept.\nen: ", entry.Note);
        Assert.Equal("one\ntwo", entry.Target.Single().Note);
        Assert.Equal("", entry.Source.Single().Note);
        Assert.Equal(new Dictionary<string, int> { ["langSet xml:lang=\"de\""] = 1 }, imported.Dropped);
    }

    [Fact]
    public async Task RegionalVariants_AreTaken_TheExactTagFirst()
    {
        var imported = await ReadAsync(Tbx2("""
            <langSet xml:lang="en-US"><tig><term>color</term></tig></langSet>
            <langSet xml:lang="EN"><tig><term>colour</term></tig></langSet>
            <langSet xml:lang="hu-HU"><tig><term>szín</term></tig></langSet>
            <langSet xml:lang="hu-RO"><tig><term>szin</term></tig></langSet>
            <langSet xml:lang="hu-HU"><tig><term>színes</term></tig></langSet>
            """));

        var entry = imported.Entries.Single();
        Assert.Equal([new Term("colour")], entry.Source);
        Assert.Equal([new Term("szín"), new Term("színes")], entry.Target);
        Assert.Equal(new Dictionary<string, int> { ["langSet xml:lang=\"en-US\""] = 1, ["langSet xml:lang=\"hu-RO\""] = 1 }, imported.Dropped);
    }

    [Theory]
    [InlineData("<langSet xml:lang=\"hu\"><tig><term>mappa</term></tig></langSet>", "no en term.")]
    [InlineData("<langSet xml:lang=\"en\"><tig><term>a</term></tig></langSet><langSet xml:lang=\"de\"><tig><term>b</term></tig></langSet>", "no hu term.")]
    [InlineData("<langSet><tig><term>a</term></tig></langSet>", "a <langSet> has no xml:lang.")]
    [InlineData("<langSet xml:lang=\"en\"><tig><note>x</note></tig></langSet>", "a <tig> has no <term>.")]
    [InlineData("<langSet xml:lang=\"en\"><tig><term>a</term><term>b</term></tig></langSet>", "a <tig> has two <term>s.")]
    [InlineData("<langSet xml:lang=\"en\"><ntig/></langSet>", "an <ntig> has no <termGrp>.")]
    [InlineData("<langSet xml:lang=\"en\"><ntig><termGrp/></ntig></langSet>", "a <termGrp> has no <term>.")]
    public async Task EntriesItCantUse_AreSkippedAndReported_TheOthersKept(string langSets, string reason)
    {
        var imported = await ReadAsync(Tbx2(langSets, "<langSet xml:lang=\"en\"><tig><term>ok</term></tig></langSet>"));

        Assert.Equal([$"Entry 1: {reason}"], imported.Skipped);
        Assert.Equal("ok", imported.Entries.Single().Source.Single().Text);
    }

    [Fact]
    public async Task TermText_IsKeptAsWritten_HiIncluded()
    {
        var imported = await ReadAsync(Tbx2("<langSet xml:lang=\"en\"><tig><term> a <hi>b</hi>&#xD;\n</term></tig><tig><term> </term></tig><tig><term/></tig></langSet>"));

        Assert.Equal([" a b\r\n", " ", ""], imported.Entries.Single().Source.Select(t => t.Text));
    }

    [Theory]
    [InlineData("2025-03-14", 0L)]
    [InlineData("2025-03-14T00:00:00", 0L)]
    [InlineData("2025-03-14T00:00:00Z", 0L)]
    [InlineData("2025-03-14T02:00:00+02:00", 0L)]
    [InlineData("2025-03-14T00:00", 0L)]
    [InlineData("2025-03-14 00:00:00", 0L)]
    [InlineData("2025-03-14T00:00:00.0000001Z", 1L)]
    public async Task Dates_AreRead_AsUtc(string date, long ticks)
    {
        var imported = await ReadAsync(Tbx2($"<transacGrp><transac type=\"transactionType\">modification</transac><date>{date}</date></transacGrp><langSet xml:lang=\"en\"><tig><term>a</term></tig></langSet>"));

        var changed = imported.Entries.Single().Changed;
        Assert.Equal(new DateTimeOffset(2025, 3, 14, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks), changed);
        Assert.Equal(TimeSpan.Zero, changed.Offset);
    }

    [Fact]
    public async Task ADateItCantRead_IsReported_AndTheEntryKept()
    {
        var imported = await ReadAsync(Tbx2("<transacGrp><transac type=\"transactionType\">origination</transac><date>14/03/2025</date><transacNote type=\"responsibility\">anna</transacNote></transacGrp><langSet xml:lang=\"en\"><tig><term>a</term></tig></langSet>"));

        Assert.Equal((default, "anna"), (imported.Entries.Single().Changed, imported.Entries.Single().ChangedBy));
        Assert.Equal(["Entry 1: the date \"14/03/2025\" can't be read; the entry is imported without one."], imported.Notices);
    }

    // ---------------------------------------------------------------- untrusted input

    // Most TBX 2 files name a DTD that isn't there; it's never fetched, from disk or the network.
    [Theory]
    [InlineData("""<!DOCTYPE martif SYSTEM "TBXcoreStructV02.dtd">""")]
    [InlineData("""<!DOCTYPE martif SYSTEM "http://localhost:1/TBXcoreStructV02.dtd">""")]
    [InlineData("""<!DOCTYPE martif SYSTEM "file:///etc/passwd">""")]
    [InlineData("""<!DOCTYPE martif [<!ENTITY unused "never used"><!ENTITY remote SYSTEM "http://localhost:1/x">]>""")]
    public async Task ADoctype_IsSkipped(string doctype)
    {
        var imported = await ReadAsync(Tbx2("<langSet xml:lang=\"en\"><tig><term>a</term></tig></langSet>").Replace("<martif", doctype + "<martif", StringComparison.Ordinal));

        Assert.Equal("a", imported.Entries.Single().Source.Single().Text);
    }

    // An entity only the DTD declares is never expanded: billion laughs, an external file.
    [Theory]
    [InlineData("""<!DOCTYPE martif [<!ENTITY lol "lol"><!ENTITY lol1 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"><!ENTITY lol2 "&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;"><!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;"><!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;"><!ENTITY lol5 "&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;"><!ENTITY lol6 "&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;"><!ENTITY lol7 "&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;"><!ENTITY lol8 "&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;"><!ENTITY lol9 "&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;">]>""", "&lol9;")]
    [InlineData("""<!DOCTYPE martif [<!ENTITY passwd SYSTEM "file:///etc/passwd">]>""", "&passwd;")]
    [InlineData("""<!DOCTYPE martif [<!ENTITY x "x">]>""", "&x;")]
    public async Task AnEntityOnlyTheDoctypeDeclares_FailsAsMalformed(string doctype, string reference)
    {
        var content = Tbx2($"<langSet xml:lang=\"en\"><tig><term>{reference}</term></tig></langSet>").Replace("<martif", doctype + "<martif", StringComparison.Ordinal);
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TermbaseFormatException>(() => ReadAsync(content));

        Assert.Contains("well-formed", error.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"It took {clock.Elapsed}.");
    }

    [Theory]
    [InlineData("""<TBX dialect="TBX-Min"/>""", "isn't TBX")]
    [InlineData("""<tbx type="TBX-Basic"/>""", "isn't TBX")]
    [InlineData("""<tmx version="1.4"/>""", "isn't TBX")]
    [InlineData("""<martif><text><body>""", "well-formed")]
    [InlineData("""<?xml version="1.0" encoding="windows-1250"?><martif/>""", "well-formed")]
    [InlineData("", "well-formed")]
    public async Task Import_RefusesWhatIsntTbx(string content, string reason)
    {
        var error = await Assert.ThrowsAsync<TermbaseFormatException>(() => ReadAsync(content));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_RefusesBytesThatArentTheDeclaredEncoding()
    {
        var bytes = Encoding.UTF8.GetBytes(Tbx2("<langSet xml:lang=\"en\"><tig><term>X</term></tig></langSet>"));
        bytes[Array.IndexOf(bytes, (byte)'X')] = 0xFF;

        await Assert.ThrowsAsync<TermbaseFormatException>(() => Tbx.ReadAsync(new MemoryStream(bytes), "en", "hu"));
    }

    [Fact]
    public async Task Import_StopsAtTheSizeLimit()
    {
        var content = Tbx2([.. Enumerable.Repeat("<langSet xml:lang=\"en\"><tig><term>a</term></tig></langSet>", 1000)]);

        var error = await Assert.ThrowsAsync<TermbaseFormatException>(() => Tbx.ReadAsync(Stream(content), "en", "hu", maxInputBytes: 1000));
        Assert.Contains("1,000-byte limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_StopsAtTheNestingLimit()
    {
        var content = Tbx2("<langSet xml:lang=\"en\"><tig><term>" + string.Concat(Enumerable.Repeat("<hi>", 5000)) + string.Concat(Enumerable.Repeat("</hi>", 5000)) + "</term></tig></langSet>");

        var error = await Assert.ThrowsAsync<TermbaseFormatException>(() => ReadAsync(content));
        Assert.Contains($"more than {Tbx.MaxElementDepth} deep", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- export

    private static readonly TermEntry[] Small =
    [
        new TermEntry(
            [new Term("folder", TermStatus.Preferred), new Term("directory") { Note = "CLI" }],
            [new Term("mappa", TermStatus.Admitted), new Term("könyvtár", TermStatus.Forbidden)],
            "Line one\r\nline two",
            Monday,
            "anna"),
        new TermEntry([new Term("SchrodingersCAT")], [], null, default, null),
    ];

    [Fact]
    public async Task Tbx2Export_IsTbxBasic_WithoutADoctype()
    {
        var (xml, _) = await WriteAsync(Small, TbxVersion.Tbx2);

        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <martif type="TBX-Basic" xml:lang="en">
              <martifHeader>
                <fileDesc>
                  <sourceDesc>
                    <p>SchrodingersCAT</p>
                  </sourceDesc>
                </fileDesc>
                <encodingDesc>
                  <p type="XCSURI">TBXBasicXCSV02.xcs</p>
                </encodingDesc>
              </martifHeader>
              <text>
                <body>
                  <termEntry id="c1">
                    <transacGrp>
                      <transac type="transactionType">modification</transac>
                      <date>2026-09-28T09:00:00Z</date>
                      <transacNote type="responsibility">anna</transacNote>
                    </transacGrp>
                    <note>Line one&#xD;
            line two</note>
                    <langSet xml:lang="en">
                      <tig>
                        <term>folder</term>
                        <termNote type="administrativeStatus">preferredTerm-admn-sts</termNote>
                      </tig>
                      <tig>
                        <term>directory</term>
                        <note>CLI</note>
                      </tig>
                    </langSet>
                    <langSet xml:lang="hu">
                      <tig>
                        <term>mappa</term>
                        <termNote type="administrativeStatus">admittedTerm-admn-sts</termNote>
                      </tig>
                      <tig>
                        <term>könyvtár</term>
                        <termNote type="administrativeStatus">deprecatedTerm-admn-sts</termNote>
                      </tig>
                    </langSet>
                  </termEntry>
                  <termEntry id="c2">
                    <langSet xml:lang="en">
                      <tig>
                        <term>SchrodingersCAT</term>
                      </tig>
                    </langSet>
                  </termEntry>
                </body>
              </text>
            </martif>
            """.Replace("\r\n", "\n", StringComparison.Ordinal), xml);
    }

    [Fact]
    public async Task Tbx3Export_IsTbxBasicDca()
    {
        var (xml, _) = await WriteAsync(Small[1..], TbxVersion.Tbx3);

        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <tbx type="TBX-Basic" style="dca" xml:lang="en" xmlns="urn:iso:std:iso:30042:ed-2">
              <tbxHeader>
                <fileDesc>
                  <sourceDesc>
                    <p>SchrodingersCAT</p>
                  </sourceDesc>
                </fileDesc>
              </tbxHeader>
              <text>
                <body>
                  <conceptEntry id="c1">
                    <langSec xml:lang="en">
                      <termSec>
                        <term>SchrodingersCAT</term>
                      </termSec>
                    </langSec>
                  </conceptEntry>
                </body>
              </text>
            </tbx>
            """, xml);
    }

    [Theory]
    [InlineData(TbxVersion.Tbx2)]
    [InlineData(TbxVersion.Tbx3)]
    public async Task ExportThenImport_GivesBackTheEntries(TbxVersion version)
    {
        var (xml, export) = await WriteAsync(Small, version);

        Assert.Equal(2, export.Written);
        var imported = await ReadAsync(xml);
        Assert.Equal(Small, imported.Entries);
        Assert.Empty(imported.Skipped);
        Assert.Empty(imported.Notices);
        Assert.Empty(imported.Dropped);
    }

    [Fact]
    public async Task Export_SkipsTextXmlCantHold_AndSaysSo()
    {
        TermEntry[] entries =
        [
            new TermEntry([new Term("ok")], [new Term("jó")], null, default, null),
            new TermEntry([new Term("bad\uD800")], [], null, default, null),
            new TermEntry([new Term("ok")], [], null, default, "\u0001"),
        ];

        var (xml, export) = await WriteAsync(entries, TbxVersion.Tbx2);

        Assert.Equal(1, export.Written);
        Assert.Equal(["Entry 2: its text has characters XML can't hold.", "Entry 3: its text has characters XML can't hold."], export.Skipped);
        Assert.Empty(export.Incomplete);
        Assert.Single((await ReadAsync(xml)).Entries);
    }

    // An import goes into the termbase in one AddRange: one snapshot, ids ascending in the file's order.
    [Fact]
    public async Task Import_GoesIntoTheTermbaseInOneChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schrodingerscat-{Guid.NewGuid():N}.sctb");
        try
        {
            using var termbase = SqliteTermbase.Create(path, "en", "hu");
            var imported = await ReadSampleAsync("tbx2-basic.tbx");

            var ids = termbase.AddRange(imported.Entries);

            Assert.Equal([1L, 2, 3, 4], ids.Select(id => id.Value));
            Assert.Equal(imported.Entries, termbase.Index.Entries().Select(e => e.Entry));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- helpers

    internal static string Tbx2(params string[] entries) =>
        "<martif type=\"TBX-Basic\" xml:lang=\"en\"><text><body>"
        + string.Concat(entries.Select(e => $"<termEntry>{e}</termEntry>"))
        + "</body></text></martif>";

    private static MemoryStream Stream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static Task<TermImport> ReadAsync(string content) => Tbx.ReadAsync(Stream(content), "en", "hu");

    internal static async Task<TermImport> ReadSampleAsync(string name)
    {
        await using var stream = File.OpenRead(Sample(name));
        return await Tbx.ReadAsync(stream, "en", "hu");
    }

    internal static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "termbase", name);

    private static async Task<(string Xml, TermExport Export)> WriteAsync(IEnumerable<TermEntry> entries, TbxVersion version)
    {
        using var stream = new MemoryStream();
        var export = await Tbx.WriteAsync(stream, entries, "en", "hu", version);
        return (Encoding.UTF8.GetString(stream.ToArray()), export);
    }
}
