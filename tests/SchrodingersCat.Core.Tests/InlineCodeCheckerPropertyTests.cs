using CsCheck;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Tests.Properties;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// <see cref="InlineCodeChecker"/> over generated documents (see <c>Xliff20PropertyTests</c> for how to replay
/// failures with <c>CsCheck_Seed</c>).
/// </summary>
public class InlineCodeCheckerPropertyTests
{
    [Fact]
    public void TargetEqualToTheSource_HasNoIssues() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units)
            {
                foreach (var segment in unit.Segments)
                    segment.Target = segment.Source;

                var issues = InlineCodeChecker.Check(unit);
                Assert.True(issues.Count == 0, $"Unit '{unit.Id}': {string.Join(" | ", issues.Select(i => $"{i.Kind}: {i.Message}"))}");
            }
        });

    // The generated targets have nothing to do with their sources, like a target in the middle of being edited.
    [Fact]
    public void ArbitraryTargets_NeverMakeTheCheckerThrow() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units)
            {
                foreach (var issue in InlineCodeChecker.Check(unit))
                {
                    Assert.Contains(issue.Segment, unit.Segments);
                    Assert.False(string.IsNullOrEmpty(issue.Message));
                }
            }
        });
}
