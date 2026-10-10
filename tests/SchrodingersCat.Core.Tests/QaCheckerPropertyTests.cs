using CsCheck;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.TestSupport;
using System.Xml;
using System.Xml.Linq;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// <see cref="QaChecker"/> over generated documents (see <c>Xliff20PropertyTests</c> for how to replay failures with
/// <c>CsCheck_Seed</c>).
/// </summary>
public class QaCheckerPropertyTests
{
    // The generated targets have nothing to do with their sources, like a target in the middle of being edited.
    [Fact]
    public void ArbitraryTargets_NeverMakeTheCheckerThrow_AndIssuesPointIntoTheirSegment() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units)
            {
                foreach (var issue in QaChecker.Check(unit))
                {
                    Assert.Contains(issue.Segment, unit.Segments);
                    Assert.False(string.IsNullOrEmpty(issue.Message));
                    Assert.NotEqual(IssueSeverity.Error, issue.Severity);
                    if (issue.Span is { } span)
                    {
                        var content = issue.SpanInSource ? issue.Segment.Source : issue.Segment.Target!;
                        Assert.InRange(span.Element, 0, content.Count - 1);
                        if (content[span.Element] is InlineText text)
                            Assert.InRange(span.Start + span.Length, span.Start, text.Text.Length);
                    }
                }
            }
        });

    // A copied source has nothing wrong but being a copy: no number, protected text, word, space, punctuation or
    // bracket differs from the source.
    [Fact]
    public void TargetEqualToTheSource_IsAtMostTheSameAsTheSource() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units)
            {
                foreach (var segment in unit.Segments)
                    segment.Target = segment.Source;

                var issues = QaChecker.Check(unit).Where(i => i.Kind != QaIssueKind.SameAsSource).ToList();
                Assert.True(issues.Count == 0, $"Unit '{unit.Id}': {string.Join(" | ", issues.Select(i => $"{i.Kind}: {i.Message}"))}");
            }
        });

    [Fact]
    public void TheDocumentChecks_NeverThrow_AndPointIntoTheDocument() =>
        XliffGen.Document.Sample(document =>
        {
            var segments = document.Units.SelectMany(u => u.Segments).ToHashSet();
            foreach (var issue in DocumentChecker.Check(document))
            {
                Assert.Contains(issue.Segment, segments);
                Assert.All(issue.Related, s => Assert.Contains(s, segments));
                Assert.False(string.IsNullOrEmpty(issue.Message));
            }
        });

    // Every segment translated, each the same way as its source: nothing untranslated, nothing inconsistent.
    [Fact]
    public void ADocumentOfCopiedSources_HasNoDocumentIssues() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var segment in document.Units.SelectMany(u => u.Segments))
                segment.Target = segment.Source;

            var issues = DocumentChecker.Check(document);
            Assert.True(issues.Count == 0, string.Join(" | ", issues.Select(i => $"{i.Kind}: {i.Message}")));
        });

    // A glossary entry made of a word of the source and a word of the target is always satisfied.
    [Fact]
    public void AGlossaryEntryWhoseTranslationIsInTheTarget_ReportsNothing() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units.Where(u => u.Segments.Count(s => !s.IsIgnorable && s.Target is not null) == 1))
            {
                var segment = unit.Segments.Single(s => !s.IsIgnorable && s.Target is not null);
                var source = QaText.Of(segment.Source).Text;
                var target = QaText.Of(segment.Target!).Text;
                var term = FirstWord(source);
                var translation = FirstWord(target);
                if (term is null || translation is null)
                    continue;

                XNamespace gls = GlossaryReader.Namespace;
                unit.ExtensionElements.Add(new XElement(gls + "glossary",
                    new XElement(gls + "glossEntry", new XElement(gls + "term", term), new XElement(gls + "translation", translation))));

                Assert.DoesNotContain(QaChecker.Check(unit), i => i.Kind == QaIssueKind.Terminology);
            }
        });

    // Terms and translations cut from anywhere in the unit's texts (half words, punctuation, whitespace, lone
    // surrogates) must never make the check throw, and its issues still point into their segment.
    [Fact]
    public void ArbitraryTermRecords_NeverMakeTheCheckerThrow() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units)
            {
                var texts = unit.Segments.SelectMany(s => new[] { s.Source, s.Target ?? InlineContent.Empty })
                    .Select(c => QaText.Of(c).Text).Where(t => t.Length > 0).ToList();
                var pieces = texts.SelectMany(Pieces).Distinct().ToList();
                var records = pieces.Select((piece, i) => new TermRecord(
                    [piece, .. pieces.Skip(i + 1).Take(1)],
                    [.. pieces.Skip(i + 2).Take(2)],
                    i % 2 == 0 ? TermOrigin.OfTermbase("tb") : TermOrigin.FileGlossary)
                {
                    ForbiddenTranslations = [.. pieces.Skip(i + 3).Take(1)],
                }).ToList();

                foreach (var issue in QaChecker.Check(unit, terms: new FixedTerms(records)))
                {
                    Assert.Contains(issue.Segment, unit.Segments);
                    if (issue.Span is { } span)
                    {
                        var content = issue.SpanInSource ? issue.Segment.Source : issue.Segment.Target!;
                        Assert.InRange(span.Element, 0, content.Count - 1);
                        if (content[span.Element] is InlineText text)
                            Assert.InRange(span.Start + span.Length, span.Start, text.Text.Length);
                    }
                }
            }
        });

    // A record made of a term of the source and a term of the target is always satisfied, punctuation included; and
    // the same term with a translation the target doesn't have is reported, so the term was really found.
    [Fact]
    public void ATermRecordWhoseTranslationIsInTheTarget_ReportsNothing() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units.Where(u => u.Translate != false))
            {
                foreach (var segment in unit.Segments.Where(s => !s.IsIgnorable && s.Target is not null))
                {
                    var term = FirstTerm(QaText.Of(segment.Source).Text);
                    var translation = FirstTerm(QaText.Of(segment.Target!).Text);
                    if (term is null || translation is null)
                        continue;

                    var origin = TermOrigin.OfTermbase("tb");
                    var satisfied = QaChecker.Check(unit, terms: new FixedTerms([new TermRecord([term], [translation], origin)]));
                    Assert.DoesNotContain(satisfied, i => i.Kind == QaIssueKind.Terminology && i.Segment == segment);

                    var missing = QaChecker.Check(unit, terms: new FixedTerms([new TermRecord([term], ["\u0001never"], origin)]));
                    Assert.Contains(missing, i => i.Kind == QaIssueKind.Terminology && i.Segment == segment);
                }
            }
        });

    // Pieces of a text at a few fixed places, of a few lengths.
    private static IEnumerable<string> Pieces(string text)
    {
        foreach (var start in new[] { 0, text.Length / 3, text.Length / 2, Math.Max(0, text.Length - 4) })
        {
            foreach (var length in new[] { 1, 3, 7 })
            {
                if (start + length <= text.Length)
                    yield return text.Substring(start, length);
            }
        }
    }

    // A word with letters, and the punctuation mark right after it if a word doesn't follow that (so C# from "C# x",
    // but only C from "C#x"): a term the term rule finds where it was cut from.
    private static string? FirstTerm(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"[\p{L}\p{M}\p{N}_]+(?:[^\s\p{L}\p{M}\p{N}_\p{Cs}\uFFFC](?![\p{L}\p{M}\p{N}_]))?")
            .Select(m => m.Value)
            .FirstOrDefault(w => w.Any(char.IsLetter));

    private sealed class FixedTerms(IReadOnlyList<TermRecord> records) : ITermSource
    {
        public IReadOnlyList<TermRecord> Find(QaText source) => records;
    }

    // A word with letters, as the checks' tokenizer sees words, and as XML can hold it.
    private static string? FirstWord(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"[\p{L}\p{M}\p{N}_]+")
            .Select(m => m.Value)
            .FirstOrDefault(w => w.Any(char.IsLetter) && w.All(XmlConvert.IsXmlChar));
}
