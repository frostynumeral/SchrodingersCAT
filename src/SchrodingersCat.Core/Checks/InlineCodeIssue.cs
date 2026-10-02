using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Checks;

public enum IssueSeverity
{
    /// <summary>Worth knowing, usually intentional (e.g. codes reordered for the target language).</summary>
    Info,

    /// <summary>Allowed, but often a mistake (e.g. a deletable code left out).</summary>
    Warning,

    /// <summary>Breaks the file's rules; the client's tool may reject the translation or produce broken output.</summary>
    Error,
}

public enum InlineCodeIssueKind
{
    /// <summary>A source code that may not be deleted (<c>canDelete="no"</c>, or part of a non-reorderable sequence) is not in the target.</summary>
    MissingProtectedCode,

    /// <summary>A deletable source code is not in the target.</summary>
    MissingCode,

    /// <summary>A target code that matches no source code and isn't a recognisable copy of one.</summary>
    UnknownCode,

    /// <summary>A copy of a code that may not be copied (<c>canCopy="no"</c>, or part of a non-reorderable sequence).</summary>
    CopyNotAllowed,

    /// <summary>One half of a paired code without the other anywhere in the unit's target.</summary>
    Unpaired,

    /// <summary>The codes of a non-reorderable sequence are in a different order than in the source.</summary>
    SequenceBroken,

    /// <summary>Codes in a different order (or segment) than in the source. Normal in translation.</summary>
    Reordered,

    /// <summary>A source annotation (<c>&lt;mrk&gt;</c>, <c>&lt;sm&gt;</c>/<c>&lt;em&gt;</c>) is not in the target.</summary>
    MissingAnnotation,
}

/// <summary>One problem found by <see cref="InlineCodeChecker"/>.</summary>
/// <param name="Segment">The segment the issue shows up in: the target's segment for codes in the target, the source's for missing ones.</param>
/// <param name="CodeId">The id of the code or annotation concerned (for a copy, its own id).</param>
public sealed record InlineCodeIssue(InlineCodeIssueKind Kind, IssueSeverity Severity, Segment Segment, string CodeId, string Message);
