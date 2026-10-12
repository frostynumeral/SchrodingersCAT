using CsCheck;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Text;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// The termbase's index over generated entries and texts (<see cref="TermGen"/>, and XliffGen's documents). Replay a
/// failure with <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class TermIndexPropertyTests
{
    // Documents' sources, and terms cut from them anywhere: half words, half folds, lone surrogates.
    private static readonly Gen<(InlineContent[] Texts, StoredTermEntry[] Entries)> DocumentCase =
        from document in XliffGen.Document
        let texts = document.Units.SelectMany(u => u.Segments).Select(s => s.Source).ToArray()
        from entries in TermGen.Entries(TermGen.CutFrom([.. texts.Select(t => QaText.Of(t).Text), "file"]))
        select (texts, entries);

    // The index's candidates are every term that may be in the text, so it finds what trying every term finds.
    [Fact]
    public void Index_FindsWhatAScanOfEveryEntryFinds() =>
        Gen.Frequency((2, TermGen.Case), (1, DocumentCase)).Sample(@case =>
        {
            var index = TermIndex.Of(@case.Entries);
            foreach (var text in @case.Texts)
            {
                var source = QaText.Of(text);
                Assert.Equal(index.Scan(source), index.Find(source));
            }
        });

    // Every occurrence is a place the term rule finds that term, none starts or ends in a code, and they and the hits
    // come in the order promised.
    [Fact]
    public void EveryHit_IsInsideTheText_AndTheRuleFindsItThere() =>
        TermGen.Case.Sample(@case =>
        {
            var index = TermIndex.Of(@case.Entries);
            foreach (var text in @case.Texts)
            {
                var source = QaText.Of(text);
                var hits = index.Find(source);
                foreach (var hit in hits)
                {
                    Assert.NotEmpty(hit.Occurrences);
                    Assert.Subset(hit.MatchedTerms.ToHashSet(), hit.Occurrences.Select(o => o.Term).ToHashSet());
                    Assert.All(hit.MatchedTerms, t => Assert.NotEmpty(source.FindAll(TermPattern.Of(hit.Entry.Source[t].Text))));
                    foreach (var o in hit.Occurrences)
                    {
                        Assert.True(o.Length > 0 && o.Start >= 0 && o.Start + o.Length <= source.Text.Length);
                        Assert.Contains((o.Start, o.Length), source.FindAll(TermPattern.Of(hit.Entry.Source[o.Term].Text)));
                        Assert.False(source.IsInCode(o.Start) || source.IsInCode(o.Start + o.Length - 1));
                    }

                    Assert.Equal(hit.Occurrences.OrderBy(o => o.Start).ThenByDescending(o => o.Length), hit.Occurrences);
                    Assert.Equal(hit.Occurrences.Count, hit.Occurrences.DistinctBy(o => (o.Start, o.Length)).Count());
                    Assert.Equal(hit.MatchedTerms.Order().Distinct(), hit.MatchedTerms);
                }

                Assert.Equal(hits.OrderBy(h => h.Occurrences[0].Start).ThenByDescending(h => h.Occurrences[0].Length).ThenBy(h => h.Id.Value), hits);
                Assert.Equal(hits.Count, hits.DistinctBy(h => h.Id).Count());
            }
        });

    // Folding is each character folded; text folded already comes back as it is, without a copy.
    [Fact]
    public void Fold_IsEachCharacterFolded() =>
        Gen.Frequency((1, Gen.String), (1, TermGen.AnyTerm), (1, TermGen.Term)).Sample(text =>
        {
            Assert.Equal(string.Concat(text.Select(Words.FoldOne)), Words.Fold(text));
            if (text.All(c => Words.FoldOne(c) == c.ToString()))
                Assert.Same(text, Words.Fold(text));
        });

    // FindTerm is the first place FindAll finds; and with no codes, the check's search is the rule's.
    [Fact]
    public void FindTerm_IsTheFirstOfFindAll() =>
        Gen.Select(TermGen.Content, TermGen.AnyTerm).Sample((content, term) =>
        {
            var source = QaText.Of(content);
            var all = Words.FindAll(PreparedText.Of(source.Text), TermPattern.Of(term)).ToList();
            Assert.Equal(all.Count == 0 ? null : all[0], Words.FindTerm(source.Text, term));
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Equal(all.Order(), all);

            var plain = QaText.Of(new InlineContent(content.OfType<InlineText>()));
            Assert.Equal(Words.FindTerm(plain.Text, term), plain.FindTerm(term));
        });

    // Every snapshot of a random script of writes still finds what an index of its entries built afresh finds: later
    // writes never change an earlier snapshot.
    [Fact]
    public void Snapshots_EqualARebuild() =>
        Gen.Select(TermGen.Content.Array[1, 3], Gen.Select(Gen.Int[0, 3], Gen.Int[1, 5], TermGen.Entry(TermGen.Term)).Array[1, 12]).Sample((texts, script) =>
        {
            var index = TermIndex.Empty;
            var expected = new SortedDictionary<long, TermEntry>();
            var snapshots = new List<(TermIndex Index, StoredTermEntry[] Entries)>();
            foreach (var (operation, id, entry) in script)
            {
                switch (operation)
                {
                    case 0 or 1:
                        index = index.With(new TermEntryId(id), entry);
                        expected[id] = entry;
                        break;
                    case 2:
                        index = index.Without(new TermEntryId(id));
                        expected.Remove(id);
                        break;
                    default:
                        index = index.WithoutRange([new TermEntryId(id), new TermEntryId(id + 1)]);
                        expected.Remove(id);
                        expected.Remove(id + 1);
                        break;
                }

                snapshots.Add((index, [.. expected.Select(e => new StoredTermEntry(new TermEntryId(e.Key), e.Value))]));
            }

            foreach (var (snapshot, entries) in snapshots)
            {
                var rebuilt = TermIndex.Of(entries);
                Assert.Equal(entries, snapshot.Entries());
                Assert.Equal(entries.Length, snapshot.Count);
                foreach (var text in texts.Select(QaText.Of))
                    Assert.Equal(rebuilt.Find(text), snapshot.Find(text));
            }
        });

    // The index as a term source warns about what the check warns about with every entry as a record: the records
    // of the hits first, in hit order, so the check sees them in the same order.
    [Fact]
    public void TheTermSource_AgreesWithTheCheck() =>
        Gen.Select(TermGen.Case, TermGen.Content).Sample((@case, target) =>
        {
            var index = TermIndex.Of(@case.Entries);
            var origin = TermOrigin.OfTermbase("tb");
            foreach (var source in @case.Texts)
            {
                var unit = new TranslationUnit { Id = "u1" };
                unit.Segments.Add(new Segment { Source = source, Target = target });
                var hits = index.Find(QaText.Of(source)).Select(h => h.Id).ToList();
                var records = @case.Entries.OrderBy(e => hits.IndexOf(e.Id) is var i and >= 0 ? i : int.MaxValue)
                    .Select(e => e.Entry.ToRecord(origin)).ToList();

                Assert.Equal(Issues(unit, new FixedTerms(records)), Issues(unit, index.ToTermSource("tb")));
            }
        });

    [Fact]
    public void Lookups_NeverThrow() =>
        Gen.Select(Gen.Select(TermGen.AnyTerm.Array[1, 3], TermGen.AnyTerm.Array[0, 2]).Array[0, 6], Gen.Frequency(
            (1, TermGen.Content.Array[1, 3]),
            (1, XliffGen.Document.Select(d => d.Units.SelectMany(u => u.Segments).Select(s => s.Source).ToArray())))).Sample((entries, texts) =>
        {
            var index = TermIndex.Of(entries.Select((e, i) => new StoredTermEntry(new TermEntryId(i),
                new TermEntry([.. e.Item1.Select(t => new Term(t))], [.. e.Item2.Select(t => new Term(t))], null, DateTimeOffset.UnixEpoch, null))));
            var terms = index.ToTermSource("tb");
            foreach (var text in texts.Select(QaText.Of))
            {
                Assert.Equal(index.Scan(text), index.Find(text));
                _ = terms.Find(text);
            }
        });

    // The generators make what the properties are about: hits by each part of the rule, across codes, refused by the
    // code rule, repeated, overlapping between entries, and with several synonyms.
    [Fact]
    public void Generators_CoverWhatThePropertiesClaim()
    {
        var cases = TermGen.Case.Array[300].Single();
        var seen = new HashSet<string>();
        foreach (var (texts, entries) in cases)
        {
            var index = TermIndex.Of(entries);
            foreach (var source in texts.Select(QaText.Of))
            {
                var hits = index.Find(source);
                foreach (var hit in hits)
                {
                    foreach (var o in hit.Occurrences)
                    {
                        seen.Add($"{TermPattern.Of(hit.Entry.Source[o.Term].Text).Rule} rule");
                        if (TermPattern.Of(hit.Entry.Source[o.Term].Text).Rule == TermRule.Punctuated
                            && source.Text.AsSpan(o.Start, o.Length).Contains(QaText.CodeMark))
                            seen.Add("punctuated hit across a code");
                    }

                    if (hit.Occurrences.Count > 1)
                        seen.Add("several occurrences");
                    if (hit.MatchedTerms.Count > 1)
                        seen.Add("several synonyms");
                }

                if (hits.Any(a => hits.Any(b => a.Id != b.Id && a.Occurrences.Any(x => b.Occurrences.Any(y => x.Start < y.Start + y.Length && y.Start < x.Start + x.Length)))))
                    seen.Add("overlapping entries");

                foreach (var pattern in entries.SelectMany(e => e.Entry.Source).Select(t => TermPattern.Of(t.Text)))
                {
                    if (source.FindAll(pattern).Count() < Words.FindAll(source.Prepared, pattern).Count())
                        seen.Add("refused by the code rule");
                }
            }
        }

        string[] claimed =
        [
            "Words rule", "Punctuated rule", "Folded rule", "punctuated hit across a code", "refused by the code rule",
            "several occurrences", "overlapping entries", "several synonyms",
        ];
        var missing = claimed.Where(c => !seen.Contains(c)).ToList();
        Assert.True(missing.Count == 0, $"No generated case has: {string.Join(", ", missing)}");
    }

    private static List<(Segment, string, TextSpan?)> Issues(TranslationUnit unit, ITermSource terms) =>
        [.. QaChecker.Check(unit, terms: terms).Where(i => i.Kind == QaIssueKind.Terminology).Select(i => (i.Segment, i.Message, i.Span))];

    private sealed class FixedTerms(IReadOnlyList<TermRecord> records) : ITermSource
    {
        public IReadOnlyList<TermRecord> Find(QaText source) => records;
    }
}
