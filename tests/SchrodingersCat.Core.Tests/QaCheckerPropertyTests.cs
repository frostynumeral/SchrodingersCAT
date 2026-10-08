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

    // A word with letters, as the checks' tokenizer sees words, and as XML can hold it.
    private static string? FirstWord(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"[\p{L}\p{M}\p{N}_]+")
            .Select(m => m.Value)
            .FirstOrDefault(w => w.Any(char.IsLetter) && w.All(XmlConvert.IsXmlChar));
}
