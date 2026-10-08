using CsCheck;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// Matching over documents from the shared generators (<c>SchrodingersCat.TestSupport</c>).
/// Replay a failure with <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class MemoryPropertyTests
{
    private static readonly Gen<InlineContent[]> Sources =
        XliffGen.Document.Select(d => d.Units.SelectMany(u => u.Segments).Select(s => s.Source).ToArray());

    [Fact]
    public void Scores_AreSymmetricAndInRange_AndOnlyEqualSourcesScore100() =>
        Gen.Select(Sources, Sources).Sample((a, b) =>
        {
            foreach (var (x, y) in a.Zip(b))
            {
                var score = MatchScore.Compare(x, y);
                Assert.InRange(score, 0, 100);
                Assert.Equal(score, MatchScore.Compare(y, x));
                Assert.Equal(100, MatchScore.Compare(x, x));
                if (score == 100)
                    Assert.Equal(Strip(x), Strip(y));
            }
        });

    // Pairs that differ only in code ids are one entry (the newer one), so targets are compared as the TM does.
    [Fact]
    public void EveryStoredSegment_IsFoundAsAnExactMatch() =>
        XliffGen.Document.Sample(document =>
        {
            var tm = new InMemoryTranslationMemory();
            var translated = document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null).ToList();
            foreach (var segment in translated)
                tm.Add(new TmEntry(segment.Source, segment.Target!));

            foreach (var segment in translated)
                Assert.Contains(tm.Lookup(segment.Source, maxMatches: int.MaxValue), m => m.Score == 100 && Strip(m.Entry.Target) == Strip(segment.Target!));
        });

    // Transferring a segment's own entry onto its own source keeps every code the target has from that source, in
    // place, and leaves out only annotations and codes the source doesn't have.
    [Fact]
    public void TransferringOntoItsOwnSource_KeepsTheTargetsCodes() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var segment in document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null))
            {
                var sourceHalves = segment.Source.OfType<InlineCode>().Select(c => (c.Id, c.Kind)).ToHashSet();
                var used = new HashSet<(string, InlineCodeKind)>();
                var expected = new InlineContent(segment.Target!.Where(e => e switch
                {
                    InlineText => true,
                    InlineCode c => sourceHalves.Contains((c.Id, c.Kind)) && used.Add((c.Id, c.Kind)),
                    _ => false,
                }).Select(e => e is InlineCode c ? segment.Source.OfType<InlineCode>().First(s => s.Id == c.Id && s.Kind == c.Kind) : e));

                var result = CodeTransfer.Apply(new TmEntry(segment.Source, segment.Target!), segment.Source);

                Assert.Equal(expected, result.Target);
                Assert.Equal(used.SetEquals(sourceHalves) && segment.Target!.OfType<InlineCode>().Count() == used.Count, result.AllCodesPlaced);
            }
        });

    // The diff is the match's source with the current source's extra words: leaving out the added parts gives the
    // match's source exactly (without annotations), leaving out the removed parts gives the current source (ignoring
    // whitespace, and code ids, as the diff does).
    [Fact]
    public void Diffs_RebuildBothSources() =>
        Gen.Select(Sources, Sources).Sample((a, b) =>
        {
            foreach (var (current, match) in a.Zip(b))
            {
                var parts = SourceDiff.Compare(current, match);

                var matchSide = new InlineContent(parts.Where(p => p.Kind != DiffKind.Added).Select(p => p.Element));
                Assert.Equal(string.Concat(match.OfType<InlineText>().Select(t => t.Text)), matchSide.ToPlainText());
                Assert.Equal(match.OfType<InlineCode>(), matchSide.OfType<InlineCode>());

                var currentSide = new InlineContent(parts.Where(p => p.Kind != DiffKind.Removed).Select(p => p.Element));
                Assert.Equal(NoSpace(Strip(current)), NoSpace(Strip(currentSide)));
            }
        });

    // A query made of the beginnings of an entry's own consecutive words finds that entry; every hit's parts rebuild
    // its content exactly; and the SQLite TM finds what the in-memory TM finds.
    [Fact]
    public void Concordance_FindsTheEntryAQueryCameFrom_AndItsPartsRebuildIt() =>
        Gen.Select(XliffGen.Document, Gen.Int[0, 1_000_000]).Sample((document, choice) =>
        {
            var entries = document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null)
                .Select((s, i) => new TmEntry(s.Source, s.Target!) { Changed = DateTimeOffset.UnixEpoch.AddMinutes(i) }).ToList();
            var memory = new InMemoryTranslationMemory();
            memory.AddRange(entries);
            var stored = memory.Entries().ToList();
            if (stored.Count == 0)
                return;

            var side = choice % 2 == 0 ? ConcordanceSide.Source : ConcordanceSide.Target;
            var from = stored[choice % stored.Count];
            var content = side == ConcordanceSide.Source ? from.Source : from.Target;
            var words = content.OfType<InlineText>().SelectMany(t => Concordance.Words(t.Text).Select(w => t.Text.Substring(w.Start, w.Length))).ToList();
            if (words.Count == 0)
                return;
            var first = choice / 2 % words.Count;
            var query = ConcordanceQuery.Parse(string.Join(" ", words.Skip(first).Take(2).Select(w => w[..(1 + (choice % w.Length))])));
            if (query.IsEmpty)
                return;

            var result = Concordance.Search(memory, query, side, maxHits: stored.Count);
            Assert.Contains(result.Hits, h => h.Entry == from);
            Assert.Equal(result.Total, result.Hits.Count);
            foreach (var hit in result.Hits)
            {
                Assert.Equal(hit.Entry.Source, new InlineContent(hit.Source.Select(p => p.Element)));
                Assert.Equal(hit.Entry.Target, new InlineContent(hit.Target.Select(p => p.Element)));
                Assert.Contains(side == ConcordanceSide.Source ? hit.Source : hit.Target, p => p.IsHit);
            }

            var path = Path.Combine(Path.GetTempPath(), $"schrodingerscat-{Guid.NewGuid():N}.sctm");
            try
            {
                using var sqlite = SqliteTranslationMemory.Create(path, "en", "hu");
                sqlite.AddRange(stored);
                var fromSqlite = Concordance.Search(sqlite, query, side, maxHits: stored.Count);
                Assert.Equal(result.Hits.Select(h => (h.Entry, h.Source, h.Target)), fromSqlite.Hits.Select(h => (h.Entry, h.Source, h.Target)), new HitComparer());
            }
            finally
            {
                File.Delete(path);
            }
        });

    private sealed class HitComparer : IEqualityComparer<(TmEntry, IReadOnlyList<HighlightPart>, IReadOnlyList<HighlightPart>)>
    {
        public bool Equals((TmEntry, IReadOnlyList<HighlightPart>, IReadOnlyList<HighlightPart>) x, (TmEntry, IReadOnlyList<HighlightPart>, IReadOnlyList<HighlightPart>) y) =>
            x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2) && x.Item3.SequenceEqual(y.Item3);

        public int GetHashCode((TmEntry, IReadOnlyList<HighlightPart>, IReadOnlyList<HighlightPart>) obj) => obj.Item1.GetHashCode();
    }

    private static string NoSpace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    // What the score can't see: annotations, and code ids and attributes.
    private static string Strip(InlineContent content) => string.Concat(content.Select(e => e switch
    {
        InlineText t => Nfc(t.Text),
        InlineCode c => $"{(int)c.Kind}",
        _ => "",
    }));

    private static string Nfc(string text)
    {
        try
        {
            return text.Normalize();
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
