using System.Globalization;
using System.Text;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class TermCsvTests
{
    // ---------------------------------------------------------------- the samples

    [Fact]
    public async Task Utf8BomCommaSample_IsRead()
    {
        var (table, imported) = await ImportSampleAsync("utf8-bom-comma.csv", new CsvColumns(0, 1, 2, 3));

        Assert.Equal((',', "utf-8"), (table.Separator, table.Encoding.WebName));
        Assert.Equal(
        [
            new TermEntry([new Term("folder", TermStatus.Preferred)], [new Term("mappa", TermStatus.Preferred)], "A container, for files", default, null),
            new TermEntry([new Term("directory", TermStatus.Admitted), new Term("folder", TermStatus.Admitted)], [new Term("könyvtár", TermStatus.Admitted)], null, default, null),
            new TermEntry([new Term("say \"hi\"")], [new Term("köszönj")], null, default, null),
        ], imported.Entries);
        Assert.Empty(imported.Skipped);
        Assert.Empty(imported.Notices);
    }

    [Fact]
    public async Task Utf8SemicolonSample_KeepsLineBreaksInQuotes_AndPassesOverBlankRows()
    {
        var (table, imported) = await ImportSampleAsync("utf8-semicolon.csv", new CsvColumns(0, 1, 2, null));

        Assert.Equal(';', table.Separator);
        Assert.Equal(
        [
            new TermEntry([new Term("file")], [new Term("fájl")], "First line\nsecond line", default, null),
            new TermEntry([new Term("printer")], [new Term("nyomtató")], null, default, null),
            new TermEntry([new Term("scanner")], [new Term("szkenner")], "Bring", default, null),
        ], imported.Entries);
        Assert.Empty(imported.Skipped);
    }

    [Fact]
    public async Task Utf16TabSample_IsRead_WithRegionalHeaders()
    {
        var (table, imported) = await ImportSampleAsync("utf16-tab.txt", new CsvColumns(0, 1, null, 2));

        Assert.Equal(('\t', "utf-16"), (table.Separator, table.Encoding.WebName));
        Assert.Equal(
        [
            new TermEntry([new Term("color", TermStatus.Preferred)], [new Term("szín", TermStatus.Preferred)], null, default, null),
            new TermEntry([new Term("gray"), new Term("grey")], [new Term("szürke")], null, default, null),
        ], imported.Entries);
    }

    [Fact]
    public async Task Windows1250Sample_IsntUtf8_AndReadsWithItsEncoding()
    {
        var bytes = await File.ReadAllBytesAsync(TbxTests.Sample("windows-1250.csv"));

        Assert.Null(TermCsv.DetectEncoding(bytes));
        var error = Assert.Throws<TermbaseFormatException>(() => TermCsv.Parse(bytes));
        Assert.Equal("The file isn't valid utf-8; choose its encoding.", error.Message);

        var table = TermCsv.Parse(bytes, TermCsv.LegacyEncoding(1250));
        Assert.Equal(["English", "magyar", "Note"], table.Rows[0]);
        var imported = TermCsv.Import(table, new CsvColumns(0, 1, 2, null));
        Assert.Equal(
        [
            new TermEntry([new Term("willow")], [new Term("fűzfa")], null, default, null),
            new TermEntry([new Term("guard")], [new Term("őr")], "Őrség: guard duty", default, null),
        ], imported.Entries);
    }

    [Fact]
    public async Task FormulasAndPipesSample_StripsOnlyTheExportsQuote_SplitsTerms_AndReports()
    {
        var (_, imported) = await ImportSampleAsync("formulas-and-pipes.csv", new CsvColumns(0, 1, 2, 3));

        Assert.Equal(
        [
            new TermEntry([new Term("=SUM(A1)")], [new Term("=SZUM(A1)")], null, default, null),
            new TermEntry([new Term("'til")], [new Term("'til")], "The client's own apostrophe.", default, null),
            new TermEntry([new Term("folder"), new Term("directory")], [new Term("mappa")], null, default, null),
            new TermEntry([new Term("save")], [new Term("mentés")], null, default, null),
            new TermEntry([new Term("@user")], [new Term("'+1")], null, default, null),
        ], imported.Entries);
        Assert.Equal(["Row 6: no source term."], imported.Skipped);
        Assert.Equal(["Row 5: the status \"approved\" isn't known; the terms are imported without one."], imported.Notices);
    }

    // ---------------------------------------------------------------- encodings

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, "utf-8")]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0 }, "utf-16")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'a' }, "utf-16BE")]
    public void ABom_DecidesTheEncoding_WhateverTheUserChose(byte[] bytes, string encoding)
    {
        Assert.Equal(encoding, TermCsv.DetectEncoding(bytes)?.WebName);

        var table = TermCsv.Parse(bytes, TermCsv.LegacyEncoding(1250));
        Assert.Equal(encoding, table.Encoding.WebName);
        Assert.Equal(["a"], Assert.Single(table.Rows));
    }

    [Fact]
    public void WithoutABom_ValidUtf8IsUtf8_AndTheUsersChoiceIsUsed()
    {
        byte[] bytes = [(byte)'a', 0xC3, 0xA1];

        Assert.Equal("utf-8", TermCsv.DetectEncoding(bytes)?.WebName);
        Assert.Equal("utf-8", TermCsv.DetectEncoding([])?.WebName);
        Assert.Equal(["aá"], TermCsv.Parse(bytes).Rows.Single());
        Assert.Equal(["aĂˇ"], TermCsv.Parse(bytes, TermCsv.LegacyEncoding(1250)).Rows.Single());
    }

    [Fact]
    public void BytesThatArentValid_AreNeverReplaced()
    {
        Assert.Null(TermCsv.DetectEncoding([(byte)'a', 0xF5]));
        Assert.Throws<TermbaseFormatException>(() => TermCsv.Parse([(byte)'a', 0xF5]));
        Assert.Equal("The file isn't valid utf-16.", Assert.Throws<TermbaseFormatException>(() => TermCsv.Parse([(byte)'a', 0, 0x00, 0xD8], Encoding.Unicode)).Message);
        Assert.Throws<TermbaseFormatException>(() => TermCsv.Parse([0xFF, 0xFE, (byte)'a']));
    }

    [Fact]
    public async Task ReadBytes_StopsAtTheSizeLimit()
    {
        var error = await Assert.ThrowsAsync<TermbaseFormatException>(() => TermCsv.ReadBytesAsync(new MemoryStream(new byte[2000]), maxInputBytes: 1000));

        Assert.Contains("1,000-byte limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(1000, (await TermCsv.ReadBytesAsync(new MemoryStream(new byte[1000]), maxInputBytes: 1000)).Length);
    }

    // ---------------------------------------------------------------- parsing

    [Theory]
    [InlineData("a,b;c\td", '\t')]
    [InlineData("a;b,c", ';')]
    [InlineData("a,b,c;d", ',')]
    [InlineData("a;b;c,d", ';')]
    [InlineData("a", ',')]
    [InlineData("\"a;b;c\",d\ne;f;g", ',')]
    public void TheSeparator_IsTheFirstRowsCommonest_TabThenSemicolonOnATie(string text, char separator) =>
        Assert.Equal(separator, TermCsv.Parse(Encoding.UTF8.GetBytes(text)).Separator);

    [Theory]
    [InlineData("a,b\r\nc,d\r\n", new[] { "a|b", "c|d" })]
    [InlineData("a,b\nc,d", new[] { "a|b", "c|d" })]
    [InlineData("a,b\rc,d\r", new[] { "a|b", "c|d" })]
    [InlineData("\"a\r\nb\",\"c\"\"d\"\r\n", new[] { "a\r\nb|c\"d" })]
    [InlineData("\"a\rb\nc\",", new[] { "a\rb\nc|" })]
    [InlineData("a\"b,\"c\"d", new[] { "a\"b|cd" })]
    [InlineData(",\n\n", new[] { "|", "" })]
    [InlineData("", new string[0])]
    public void Rows_FollowRfc4180_Leniently(string text, string[] rows) =>
        Assert.Equal(rows, TermCsv.Parse(Encoding.UTF8.GetBytes(text)).Rows.Select(r => string.Join('|', r)));

    [Fact]
    public void AQuotedFieldThatNeverCloses_Fails()
    {
        var error = Assert.Throws<TermbaseFormatException>(() => TermCsv.Parse(Encoding.UTF8.GetBytes("a,b\n\"c,d\ne,f")));

        Assert.Equal("A quoted field isn't closed (row 2).", error.Message);
    }

    // ---------------------------------------------------------------- columns

    [Theory]
    [InlineData(new[] { "en", "hu", "Note", "Status" }, "en", "hu", 0, 1, 2, 3)]
    [InlineData(new[] { "Status", "Comments", " HU-hu ", "en-GB" }, "en", "hu", 3, 2, 1, 0)]
    [InlineData(new[] { "en", "definition", "hu" }, "en-US", "hu", 0, 2, 1, null)]
    [InlineData(new[] { "en-US", "en-GB", "hu" }, "en-GB", "hu", 1, 2, null, null)]
    public void Columns_AreGuessedFromLanguageTags(string[] header, string source, string target, int s, int t, int? note, int? status) =>
        Assert.Equal(new CsvColumns(s, t, note, status), TermCsv.GuessColumns(header, source, target));

    [Fact]
    public void Columns_AreGuessedFromLanguageNames_WhereTheSystemKnowsThem()
    {
        if (!KnowsLanguageNames)
            return; // invariant globalization: only tags

        Assert.Equal(new CsvColumns(1, 0, null, null), TermCsv.GuessColumns(["magyar", "English (United States)"], "en-US", "hu"));
        Assert.Equal(new CsvColumns(0, 1, null, null), TermCsv.GuessColumns(["english", "Hungarian"], "en", "hu"));
    }

    [Theory]
    [InlineData("en,de")]
    [InlineData("hu")]
    [InlineData("en,")]
    [InlineData("")]
    public void Columns_AreNull_WithoutBothLanguages(string header) =>
        Assert.Null(TermCsv.GuessColumns(header.Length == 0 ? [] : header.Split(','), "en", "hu"));

    // ---------------------------------------------------------------- import

    [Fact]
    public void Import_TakesWhatsThere_MissingCellsEmpty()
    {
        var table = new CsvTable(',', Encoding.UTF8, [["a", "b"], ["x"], ["", "y"], [" ", ""], ["  p | | q ", "r|", "", "", "extra"]]);

        var imported = TermCsv.Import(table, new CsvColumns(0, 1, 7, -1), hasHeader: false);

        Assert.Equal(
        [
            new TermEntry([new Term("a")], [new Term("b")], null, default, null),
            new TermEntry([new Term("x")], [], null, default, null),
            new TermEntry([new Term("p"), new Term("q")], [new Term("r")], null, default, null),
        ], imported.Entries);
        Assert.Equal(["Row 3: no source term."], imported.Skipped);
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public async Task Export_IsUtf8WithBom_EveryFieldQuoted_Crlf()
    {
        TermEntry[] entries =
        [
            new TermEntry([new Term("folder", TermStatus.Preferred)], [new Term("mappa", TermStatus.Preferred)], "say \"hi\"\nnow", DateTimeOffset.UnixEpoch, "anna"),
            new TermEntry([new Term("=SUM(A1)"), new Term("'til")], [new Term("''=x")], "-note", default, null),
            new TermEntry([new Term("'til")], [new Term("@")], "", default, null),
        ];
        using var stream = new MemoryStream();

        var export = await TermCsv.WriteAsync(stream, entries, "en", "hu");

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, stream.ToArray()[..3]);
        Assert.Equal(
            "\"en\",\"hu\",\"Note\",\"Status\"\r\n"
            + "\"folder\",\"mappa\",\"say \"\"hi\"\"\nnow\",\"preferred\"\r\n"
            + "\"'=SUM(A1)|'til\",\"'''=x\",\"'-note\",\"\"\r\n"
            + "\"'til\",\"'@\",\"\",\"\"\r\n",
            Encoding.UTF8.GetString(stream.ToArray()[3..]));
        Assert.Equal(3, export.Written);
        Assert.Empty(export.Skipped);
        Assert.Empty(export.Incomplete);

        // And back: every formula quote the export added comes off, a client's own stays.
        var imported = TermCsv.Import(TermCsv.Parse(stream.ToArray()), new CsvColumns(0, 1, 2, 3));
        Assert.Equal(
        [
            entries[0] with { Changed = default, ChangedBy = null },
            entries[1],
            entries[2] with { Note = null },
        ], imported.Entries);
    }

    [Fact]
    public async Task Export_LeavesOutWhatCsvCantHold_AndSaysSo()
    {
        TermEntry[] entries =
        [
            new TermEntry([new Term("folder | directory"), new Term("folder")], [new Term(" mappa ")], null, default, null),
            new TermEntry([new Term("a", TermStatus.Preferred)], [new Term("b", TermStatus.Forbidden) { Note = "no" }, new Term(" ")], null, default, null),
            new TermEntry([new Term("|")], [new Term("b")], null, default, null),
            new TermEntry([new Term("a")], [new Term("b")], "\uDC00", default, null),
        ];
        using var stream = new MemoryStream();

        var export = await TermCsv.WriteAsync(stream, entries, "en", "hu");

        Assert.Equal(2, export.Written);
        Assert.Equal(["Entry 3: no source term CSV can hold.", "Entry 4: its text has characters UTF-8 can't hold."], export.Skipped);
        Assert.Equal(
        [
            "Entry 1: written without terms with |, the whitespace around terms.",
            "Entry 2: written without blank terms, the terms' different statuses, the terms' notes.",
        ], export.Incomplete);
        Assert.Equal(
            [
                new TermEntry([new Term("folder")], [new Term("mappa")], null, default, null),
                new TermEntry([new Term("a")], [new Term("b")], null, default, null),
            ],
            TermCsv.Import(TermCsv.Parse(stream.ToArray()), new CsvColumns(0, 1, 2, 3)).Entries);
    }

    // ---------------------------------------------------------------- helpers

    // The columns the header names; language names only where the system knows them (not with invariant globalization).
    private static async Task<(CsvTable Table, TermImport Imported)> ImportSampleAsync(string name, CsvColumns expected)
    {
        await using var stream = File.OpenRead(TbxTests.Sample(name));
        var bytes = await TermCsv.ReadBytesAsync(stream);
        var table = TermCsv.Parse(bytes, TermCsv.DetectEncoding(bytes));
        var columns = TermCsv.GuessColumns(table.Rows[0], "en", "hu");
        if (columns is not null || KnowsLanguageNames)
            Assert.Equal(expected, columns);
        return (table, TermCsv.Import(table, expected));
    }

    private static bool KnowsLanguageNames => CultureInfo.GetCultures(CultureTypes.AllCultures).Any(c => c.Name == "hu");
}
