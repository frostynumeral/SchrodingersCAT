using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Checks;

/// <summary>What a quality check looks for (<c>docs/design/qa-checks.md</c>); also what a dismissal or a switched-off check names.</summary>
public enum QaIssueKind
{
    /// <summary>A number in the source isn't in the target, or the target has one the source doesn't.</summary>
    Numbers,

    /// <summary>The target is the same as the source.</summary>
    SameAsSource,

    /// <summary>Text marked <c>translate="no"</c> in the source isn't in the target unchanged.</summary>
    DoNotTranslate,

    /// <summary>The same word twice in a row in the target.</summary>
    RepeatedWord,

    /// <summary>A double space, or leading or trailing whitespace that differs from the source's.</summary>
    Spaces,

    /// <summary>The target ends with other punctuation than the source.</summary>
    EndPunctuation,

    /// <summary>A bracket or quote without its other half in the target.</summary>
    Brackets,

    /// <summary>A term from the file's glossary is in the source, and none of its translations in the target.</summary>
    Terminology,

    /// <summary>A segment to translate has no target (document check; only in the report).</summary>
    Untranslated,

    /// <summary>The same source is translated differently in the document (document check).</summary>
    InconsistentTranslation,

    /// <summary>The same translation stands for different sources in the document (document check).</summary>
    InconsistentSource,
}

/// <summary>
/// Where a problem is in a segment's content: the element (an <see cref="InlineText"/>, or the code standing in for
/// text) and a range of its text.
/// </summary>
public readonly record struct TextSpan(int Element, int Start, int Length);

/// <summary>One problem a quality check found in a segment (section 1 of <c>docs/design/qa-checks.md</c>).</summary>
/// <param name="Severity">Info or Warning: only codes can break XLIFF's rules, so no quality check reports an Error.</param>
public sealed record QaIssue(QaIssueKind Kind, IssueSeverity Severity, Segment Segment, string Message)
{
    /// <summary>Where the problem is: in the target, or in the source for something missing; null if it's not one place.</summary>
    public TextSpan? Span { get; init; }

    /// <summary>Whether <see cref="Span"/> is in the source rather than the target.</summary>
    public bool SpanInSource { get; init; }

    /// <summary>The other segments a document check found this one inconsistent with.</summary>
    public IReadOnlyList<Segment> Related { get; init; } = [];
}

/// <summary>Which quality checks run. All do by default.</summary>
public sealed record QaOptions
{
    public static QaOptions Default { get; } = new();

    /// <summary>The checks switched off.</summary>
    public IReadOnlySet<QaIssueKind> Disabled { get; init; } = new HashSet<QaIssueKind>();

    public bool IsEnabled(QaIssueKind kind) => !Disabled.Contains(kind);
}

/// <summary>One quality check: compares a segment's target with its source.</summary>
public interface IQaCheck
{
    QaIssueKind Kind { get; }

    IEnumerable<QaIssue> Check(QaSegment segment);
}
