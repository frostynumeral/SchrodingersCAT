using System.Text;
using CsCheck;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// TBX and CSV export then import, and damaged files. Replay a failure with
/// <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class TermExchangePropertyTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // What makes CSV hard: whitespace around terms, | inside them, and formula characters at the start.
    private static readonly Gen<string> CsvTerm = Gen.Frequency(
        (3, TermGen.Term),
        (1, TermGen.AnyTerm),
        (1, Gen.Select(Gen.OneOfConst("", " ", " ", "\t"), TermGen.Term, Gen.OneOfConst("", " ", "|", " | b", "\r\n"), (before, term, after) => before + term + after)),
        (1, Gen.OneOfConst("=SUM(A1)", "'=x", "''+1", "'til", "'", "-", "@", "+1", "\"", "a,b;c\td", "|")));

    // Whatever the entries hold, TBX keeps every entry XML can hold exactly (statuses, Unspecified included, notes, ""
    // and null apart, dates to the tick, users), in one AddRange, and reports the others.
    [Fact]
    public void TbxExportThenImport_KeepsEntries() =>
        Gen.Select(TermGen.FullEntry(Gen.Frequency((3, TermGen.Term), (1, TermGen.AnyTerm))).Array[0, 20], Gen.OneOfConst(TbxVersion.Tbx2, TbxVersion.Tbx3)).Sample((entries, version) =>
        {
            using var stream = new MemoryStream();
            var export = Tbx.WriteAsync(stream, entries, "en", "hu", version).GetAwaiter().GetResult();
            stream.Position = 0;
            var imported = Tbx.ReadAsync(stream, "en", "hu").GetAwaiter().GetResult();
            var termbase = new InMemoryTermbase("en", "hu");
            termbase.AddRange(imported.Entries);

            var writable = entries.Where(IsXml).Select(SqliteTermbase.Normalized).ToList();
            Assert.Equal(writable, termbase.Index.Entries().Select(e => e.Entry));
            Assert.Equal((writable.Count, entries.Length - writable.Count), (export.Written, export.Skipped.Count));
            Assert.Empty(imported.Skipped);
            Assert.Empty(imported.Notices);
            Assert.Empty(imported.Dropped);
        });

    // CSV keeps what it can hold (CsvView), and reports exactly the entries it changed or skipped.
    [Fact]
    public void CsvExportThenImport_KeepsWhatCsvCanHold() =>
        TermGen.FullEntry(CsvTerm).Array[0, 12].Sample(entries =>
        {
            using var stream = new MemoryStream();
            var export = TermCsv.WriteAsync(stream, entries, "en", "hu").GetAwaiter().GetResult();
            var bytes = stream.ToArray();
            var table = TermCsv.Parse(bytes, TermCsv.DetectEncoding(bytes));
            var columns = TermCsv.GuessColumns(table.Rows[0], "en", "hu");
            Assert.Equal(new CsvColumns(0, 1, 2, 3), columns);
            var imported = TermCsv.Import(table, columns!);

            var views = entries.Select(CsvView).ToList();
            Assert.Equal(views.OfType<TermEntry>(), imported.Entries);
            Assert.Empty(imported.Skipped);
            Assert.Empty(imported.Notices);
            Assert.Equal(views.Count(v => v is not null), export.Written);
            Assert.Equal(Numbers(views.Select((v, i) => (v, i)).Where(x => x.v is null)), Numbers(export.Skipped));
            Assert.Equal(
                Numbers(views.Select((v, i) => (v, i)).Where(x => x.v is not null && x.v != Basic(entries[x.i]))),
                Numbers(export.Incomplete));
        });

    // TBX files come from anywhere: damaged ones fail only with TermbaseFormatException, or import entries.
    [Fact]
    public void DamagedTbx_FailsOnlyWithTermbaseFormatException() =>
        Gen.Select(
            TermGen.FullEntry(TermGen.Term).Array[1, 6],
            Gen.OneOfConst(TbxVersion.Tbx2, TbxVersion.Tbx3),
            Gen.Int[0, 100_000].Array[1, 6],
            Gen.OneOfConst(
                "<", ">", "&", "&a;", "\"", "<!DOCTYPE martif [<!ENTITY a \"x\">]>", "<termEntry>", "</langSet>", "<tig/>",
                "<termNote type=\"administrativeStatus\">x</termNote>", "\u0000", "xml:lang=\"\"", "<ntig><termGrp/></ntig>",
                "<langSet xml:lang=\"hu\">", "<langSec xml:lang=\"en\"><termSec/></langSec>", "", "flip")).Sample((entries, version, positions, damage) =>
        {
            using var stream = new MemoryStream();
            Tbx.WriteAsync(stream, entries, "en", "hu", version).GetAwaiter().GetResult();
            var bytes = Damage(stream.ToArray(), positions, damage);

            try
            {
                var imported = Tbx.ReadAsync(new MemoryStream(bytes), "en", "hu").GetAwaiter().GetResult();
                Assert.All(imported.Entries, e => Assert.NotEmpty(e.Source));
            }
            catch (TermbaseFormatException)
            {
            }
        });

    // CSV files too, read in any encoding, with any columns (out of range ones are empty).
    [Fact]
    public void DamagedCsv_FailsOnlyWithTermbaseFormatException() =>
        Gen.Select(
            Gen.Frequency(
                (3, TermGen.FullEntry(CsvTerm).Array[0, 6].Select(entries =>
                {
                    using var stream = new MemoryStream();
                    TermCsv.WriteAsync(stream, entries, "en", "hu").GetAwaiter().GetResult();
                    return stream.ToArray();
                })),
                (1, Gen.Byte.Array[0, 60])),
            Gen.Int[0, 100_000].Array[1, 6],
            Gen.OneOfConst("\"", ",", ";", "\t", "\r", "\n", "'", "|", "\"\"", "", "flip"),
            Gen.Int[0, 3],
            Gen.Select(Gen.Int[-2, 6], Gen.Int[-2, 6], Gen.Int[-2, 6], Gen.Int[-2, 6])).Sample((exported, positions, damage, decoding, columns) =>
        {
            var bytes = Damage(exported, positions, damage);
            try
            {
                var table = TermCsv.Parse(bytes, decoding switch
                {
                    0 => TermCsv.DetectEncoding(bytes),
                    1 => Encoding.Unicode,
                    2 => TermCsv.LegacyEncoding(1250),
                    _ => null,
                });
                if (table.Rows.Count > 0)
                    TermCsv.GuessColumns(table.Rows[0], "en", "hu");
                var imported = TermCsv.Import(table, new CsvColumns(columns.Item1, columns.Item2, columns.Item3, columns.Item4), hasHeader: columns.Item1 % 2 == 0);
                Assert.All(imported.Entries, e => Assert.NotEmpty(e.Source));
            }
            catch (TermbaseFormatException)
            {
            }
        });

    // Inserts the damage at each position, cuts a piece out there ("") or flips a byte's bits there ("flip").
    private static byte[] Damage(byte[] bytes, int[] positions, string damage)
    {
        var list = new List<byte>(bytes);
        foreach (var position in positions)
        {
            var at = position % (list.Count + 1);
            if (damage == "flip")
            {
                if (at < list.Count)
                    list[at] ^= (byte)(position >> 8 | 1);
            }
            else if (damage.Length == 0)
            {
                list.RemoveRange(at, Math.Min(7, list.Count - at));
            }
            else
            {
                list.InsertRange(at, Encoding.UTF8.GetBytes(damage));
            }
        }

        return [.. list];
    }

    private static bool IsXml(TermEntry entry)
    {
        try
        {
            foreach (var text in entry.Source.Concat(entry.Target).SelectMany(t => new[] { t.Text, t.Note }).Concat([entry.Note, entry.ChangedBy]).OfType<string>())
                System.Xml.XmlConvert.VerifyXmlChars(text);
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    // What CSV holds of an entry, as the import gives it back; null if the export skips it.
    private static TermEntry? CsvView(TermEntry entry)
    {
        static List<Term> Kept(IReadOnlyList<Term> terms) =>
            [.. terms.Where(t => !t.Text.Contains('|', StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Text))];

        var source = Kept(entry.Source);
        var target = Kept(entry.Target);
        if (source.Count == 0)
            return null;

        var statuses = source.Concat(target).Select(t => t.Status).Distinct().ToList();
        var status = statuses.Count == 1 ? statuses[0] : TermStatus.Unspecified;
        var view = new TermEntry(
            [.. source.Select(t => new Term(t.Text.Trim(), status))],
            [.. target.Select(t => new Term(t.Text.Trim(), status))],
            string.IsNullOrEmpty(entry.Note) ? null : entry.Note,
            default,
            null);
        return view.Source.Concat(view.Target).Select(t => t.Text).Append(view.Note ?? "").All(IsUtf8) ? view : null;
    }

    // The entry as CSV would give it back if it lost nothing it reports: no date or user, an empty note as none.
    private static TermEntry Basic(TermEntry entry) =>
        entry with { Note = string.IsNullOrEmpty(entry.Note) ? null : entry.Note, Changed = default, ChangedBy = null };

    private static bool IsUtf8(string text)
    {
        try
        {
            StrictUtf8.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static List<int> Numbers(IEnumerable<(TermEntry? View, int Index)> entries) => [.. entries.Select(e => e.Index + 1)];

    // "Entry 3: …" → 3.
    private static List<int> Numbers(IEnumerable<string> report) =>
        [.. report.Select(line => int.Parse(line["Entry ".Length..line.IndexOf(':', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture))];
}
