using CsCheck;
using SchrodingersCat.App.Editing;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Steps 5 and 6 of <c>docs/design/termbase.md</c> over documents from the shared generators: the marks, the background
/// re-check, the term picker's choices, and following adds, edits and deletes. Replay a failure with <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
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

    // Step 6: after any script of adds (new entries and additions to one), edits and deletes through the window, each
    // segment's hits and each unit's issues are what a fresh lookup and a full check with the termbase give, and with
    // no limit on the units re-checked at once, no write needed the whole document checked again.
    [Fact]
    public async Task AddsEditsAndDeletes_KeepTheMarksAndChecks_AsAFreshCheckGivesThem()
    {
        var directory = Directory.CreateTempSubdirectory("schrodingerscat-");
        try
        {
            var affected = 0;
            await Gen.Select(WithTerms, Gen.Select(Gen.Int[0, 3], Gen.Int[0, int.MaxValue], Gen.Int[0, int.MaxValue]).Array[1, 6]).SampleAsync(async sample =>
            {
                var ((document, picks), script) = sample;
                var words = Words(document);
                if (words.Count == 0)
                    return;

                var path = Path.Combine(directory.FullName, $"{Guid.NewGuid():N}.xlf");
                await new Xliff20Serializer().WriteFileAsync(document, path);
                var dialogs = new FakeDialogs(null) { Confirm = true };
                var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
                await vm.LoadAsync(path);
                Assert.Null(vm.ErrorMessage);
                // In the document's languages, so it serves the document.
                var termbase = new InMemoryTermbase(document.SourceLanguage, document.TargetLanguage ?? "zxx");
                termbase.AddRange(Termbase(document, picks).Index.Entries().Select(e => e.Entry));
                await vm.UseTermbaseAsync(new OpenTermbase(termbase, "client"));
                await vm.TermCheck;
                var fullCheck = vm.TermCheck;
                // Half the samples re-check affected units in the background, as a term in most of a document does.
                var inline = picks[0] % 2 == 0;
                vm.InlineRecheckLimit = inline ? int.MaxValue : 0;

                foreach (var (operation, pick, other) in script)
                {
                    var stored = termbase.Index.Entries().ToList();
                    var entry = new TermEntry([new Term(words[pick % words.Count])], [new Term($"t{other % 5}", (TermStatus)(other % 4))], null, default, null);
                    var versions = vm.Segments.Select(s => s.Check.Version).ToList();
                    if (operation == 0 || stored.Count == 0)
                    {
                        dialogs.EditTermEntry = _ => new TermEntryEdit(null, entry, IsAddition: false);
                        await vm.AddTermCommand.ExecuteAsync(new TermSelection(null, null));
                    }
                    else
                    {
                        var target = stored[other % stored.Count];
                        var hit = new TermHit(target.Id, target.Entry, [0], []);
                        dialogs.EditTermEntry = _ => new TermEntryEdit(target.Id, entry, IsAddition: operation == 1);
                        if (operation == 1)
                            await vm.AddTermCommand.ExecuteAsync(new TermSelection(null, null));
                        else if (operation == 2)
                            await vm.EditTermEntryCommand.ExecuteAsync(hit);
                        else
                            await vm.DeleteTermEntryCommand.ExecuteAsync(hit);
                    }

                    await vm.TermCheck;
                    Assert.Null(vm.ErrorMessage);
                    if (inline)
                        Assert.Same(fullCheck, vm.TermCheck);
                    Interlocked.Add(ref affected, vm.Segments.Select((s, i) => s.Check.Version != versions[i]).Count(c => c));
                    var index = termbase.Index;
                    Assert.Same(index, vm.DocumentTerms!.Index);
                    var source = index.ToTermSource("client");
                    foreach (var segment in vm.Segments)
                    {
                        Assert.Equal(segment.Check.IsToTranslate ? index.Find(QaText.Of(segment.Source)) : [], segment.TermHits);
                        Assert.Equal(Issues(segment.Check.CheckQuality(QaOptions.Default, source)), Issues(segment.Check.QaIssues));
                    }
                }

                await vm.ShutDownAsync();
            });

            Assert.True(affected > 0, "No write affected a unit.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Words of the sources as source terms, each with a few translations of every status, some blank.
    private static InMemoryTermbase Termbase(TranslationDocument document, int[] picks)
    {
        var words = Words(document);
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

    private static List<string> Words(TranslationDocument document) =>
        [.. document.Units.SelectMany(u => u.Segments).SelectMany(s => s.Source.OfType<InlineText>())
            .SelectMany(t => t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal)];

    private static List<string> Shape(IReadOnlyList<ContentRun> runs) =>
        [string.Concat(runs.OfType<TextRun>().Select(r => r.Text)), .. runs.OfType<ChipRun>().Select(c => $"{c.CodeId} {c.Kind}")];

    private static List<(QaIssueKind, Segment, string)> Issues(IEnumerable<QaIssue> issues) =>
        [.. issues.Select(i => (i.Kind, i.Segment, i.Message))];
}
