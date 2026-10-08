using CsCheck;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.TestSupport;

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
}
