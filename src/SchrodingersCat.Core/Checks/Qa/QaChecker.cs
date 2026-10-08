using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Checks;

/// <summary>
/// The quality checks beyond codes (<c>docs/design/qa-checks.md</c>), per unit like <see cref="InlineCodeChecker"/>,
/// and on the same segments: those translated and not ignorable. Each check compares one segment's target with its
/// source. Never throws on any target, so it can run on half-edited content.
/// </summary>
public static class QaChecker
{
    /// <summary>Every segment check, in the order their issues are listed.</summary>
    public static IReadOnlyList<IQaCheck> Checks { get; } =
    [
        new NumbersCheck(),
        new DoNotTranslateCheck(),
        new SameAsSourceCheck(),
        new RepeatedWordCheck(),
        new SpacesCheck(),
        new EndPunctuationCheck(),
        new BracketsCheck(),
        new TerminologyCheck(),
    ];

    public static IReadOnlyList<QaIssue> Check(TranslationUnit unit, QaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        options ??= QaOptions.Default;

        var issues = new List<QaIssue>();
        foreach (var segment in unit.Segments.Where(s => !s.IsIgnorable && s.Target is not null))
        {
            var qa = QaSegment.Of(unit, segment);
            foreach (var check in Checks.Where(c => options.IsEnabled(c.Kind)))
                issues.AddRange(check.Check(qa));
        }

        return issues;
    }
}
