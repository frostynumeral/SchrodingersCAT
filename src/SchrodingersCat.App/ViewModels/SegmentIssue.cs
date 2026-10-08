using SchrodingersCat.Core.Checks;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// One issue of a segment, whichever checker found it (step 3 of <c>docs/design/qa-checks.md</c>): the code check
/// (<see cref="Code"/>) or a quality check (<see cref="Qa"/>). Badges, tooltips and navigation don't care which.
/// </summary>
public sealed record SegmentIssue(IssueSeverity Severity, string Message)
{
    /// <summary>The code check's issue; null for a quality check's.</summary>
    public InlineCodeIssue? Code { get; private init; }

    /// <summary>The quality check's issue; null for the code check's.</summary>
    public QaIssue? Qa { get; private init; }

    public static SegmentIssue From(InlineCodeIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new SegmentIssue(issue.Severity, issue.Message) { Code = issue };
    }

    public static SegmentIssue From(QaIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new SegmentIssue(issue.Severity, issue.Message) { Qa = issue };
    }
}
