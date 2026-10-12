using CsCheck;
using SchrodingersCat.App.Editing;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 5 of <c>docs/design/termbase.md</c> over documents from the shared generators: the marks, the background
/// re-check and the term picker's choices. Replay a failure with <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class TermbasePropertyTests
{
    // Marks anywhere, out of range and overlapping included.
    private static readonly Gen<(int Element, int Start, int Length)[]> Marks =
        Gen.Select(Gen.Int[-1, 8], Gen.Int[-3, 30], Gen.Int[-3, 30]).Array[0, 6];

    // Entries made of the documents' own words, with every status and some blank translations.
    private static readonly Gen<(TranslationDocument Document, int[] Picks)> WithTerms =
        Gen.Select(XliffGen.Document, Gen.Int[0, int.MaxValue].Array[1, 12]);

    // The marks never change the text or the chips, and a character is a termbase term exactly when a mark on its text
    // element covers it; RunAt finds the run at every position as Slice counts them.
    [Fact]
    public void Build_WithArbitraryMarks_KeepsTheContent_AndMarksExactlyWhatTheyCover() =>
        Gen.Select(XliffGen.Document, Marks).Sample(sample =>
        {
            var (document, raw) = sample;
            foreach (var unit in document.Units)
            {
                var hints = new InlineCodeHints(unit);
                foreach (var segment in unit.Segments)
                {
                    var marks = raw.Select(m => new TermMark(m.Element, m.Start, m.Length, $"tip {m.Element} {m.Start}")).ToList();
                    var plain = ContentRuns.Build(segment.Source, unit, hints, []);
                    var marked = ContentRuns.Build(segment.Source, unit, hints, [], marks);

                    Assert.Equal(Shape(plain), Shape(marked));

                    // Each character of text, with the element and offset it comes from.
                    var characters = segment.Source
                        .SelectMany((e, i) => e is InlineText t ? Enumerable.Range(0, t.Text.Length).Select(o => (Element: i, Offset: o)) : [])
                        .ToList();
                    var flags = marked.OfType<TextRun>().SelectMany(r => Enumerable.Repeat(r.Annotations.HasFlag(AnnotationStyle.TermbaseTerm), r.Text.Length)).ToList();
                    Assert.Equal(
                        characters.Select(c => marks.Any(m => m.Element == c.Element && c.Offset >= m.Start && c.Offset < (long)m.Start + m.Length)),
                        flags);

                    var positions = marked.SelectMany(r => Enumerable.Repeat(r as TextRun, r is TextRun t ? t.Text.Length : 1)).ToList();
                    for (var p = 0; p < positions.Count; p++)
                        Assert.Same(positions[p], ContentRuns.RunAt(marked, p));
                    Assert.Null(ContentRuns.RunAt(marked, positions.Count));
                    Assert.Null(ContentRuns.RunAt(marked, -1));
                }
            }
        });

    // The background re-check gives every unit what checking it with the termbase's term source gives.
    [Fact]
    public async Task Recheck_EqualsCheckingEachUnitWithTheTermSource()
    {
        var termbaseIssues = 0;
        await WithTerms.SampleAsync(async sample =>
        {
            var (document, picks) = sample;
            var termbase = Termbase(document, picks);
            var checks = document.Units.Select(u => new UnitCheck(u)).ToList();

            await UnitCheck.RecheckAsync(checks, QaOptions.Default, termbase.Index.ToTermSource("client"), Task.Run, () => true);

            foreach (var check in checks)
            {
                var expected = QaChecker.Check(check.Unit, QaOptions.Default, termbase.Index.ToTermSource("client"));
                Assert.Equal(Issues(expected), Issues(check.QaIssues));
                Interlocked.Add(ref termbaseIssues, expected.Count(i => i.Message.Contains("termbase", StringComparison.Ordinal)));
            }
        });

        Assert.True(termbaseIssues > 0, "No sample had an issue from the termbase.");
    }

    // The picker offers every allowed translation of the terms found, never a forbidden or blank one, and any choice
    // pasted into an empty target is in it.
    [Fact]
    public void TheTermChoices_AreTheAllowedTranslations_AndGoIntoTheTarget()
    {
        var offered = 0;
        WithTerms.Sample(sample =>
        {
            var (document, picks) = sample;
            var termbase = Termbase(document, picks);
            foreach (var unit in document.Units)
            {
                foreach (var segment in unit.TranslatableSegments)
                {
                    var hits = termbase.Index.Find(QaText.Of(segment.Source));
                    var choices = TermChoice.For(hits).Select(c => c.Text).ToList();
                    var allowed = hits.SelectMany(h => h.Entry.Target)
                        .Where(t => t.Status != TermStatus.Forbidden && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text).ToHashSet();

                    Assert.Equal(allowed.Order(StringComparer.Ordinal), choices.Order(StringComparer.Ordinal));
                    Assert.All(choices, c => Assert.False(string.IsNullOrWhiteSpace(c)));
                    Interlocked.Add(ref offered, choices.Count);
                    foreach (var choice in choices)
                    {
                        segment.Target = null;
                        var model = new TargetEditModel(unit, segment);
                        TermInsertion.Apply(model, choice);
                        Assert.Contains(choice, segment.Target?.ToPlainText() ?? "", StringComparison.Ordinal);
                    }
                }
            }
        });

        Assert.True(offered > 0, "No sample offered a term.");
    }

    // Words of the sources as source terms, each with a few translations of every status, some blank.
    private static InMemoryTermbase Termbase(TranslationDocument document, int[] picks)
    {
        var words = document.Units.SelectMany(u => u.Segments).SelectMany(s => s.Source.OfType<InlineText>())
            .SelectMany(t => t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToList();
        var termbase = new InMemoryTermbase("en", "hu");
        if (words.Count == 0)
            return termbase;

        foreach (var pick in picks)
        {
            var translations = Enumerable.Range(0, pick % 4).Select(i => new Term(
                (pick / 3 + i) % 6 == 0 ? " " : $"t{(pick / 5 + i) % 7}",
                (TermStatus)((pick / 11 + i) % 4))).ToList();
            termbase.Add(new TermEntry([new Term(words[pick % words.Count])], translations, null, DateTimeOffset.UnixEpoch, null));
        }

        return termbase;
    }

    private static List<string> Shape(IReadOnlyList<ContentRun> runs) =>
        [string.Concat(runs.OfType<TextRun>().Select(r => r.Text)), .. runs.OfType<ChipRun>().Select(c => $"{c.CodeId} {c.Kind}")];

    private static List<(QaIssueKind, Segment, string)> Issues(IEnumerable<QaIssue> issues) =>
        [.. issues.Select(i => (i.Kind, i.Segment, i.Message))];
}
