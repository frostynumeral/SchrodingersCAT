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

    /// <summary>
    /// The unit's issues. A unit marked <c>translate="no"</c> has none: its content is meant to stay as it is. (One
    /// whose file or group is marked so is the caller's to leave out; see
    /// <see cref="TranslationDocument.UnitsToTranslate"/>.) <paramref name="terms"/> gives the terminology check term
    /// records from outside the file, such as a termbase's, as well as the file's glossary. It isn't part of
    /// <paramref name="options"/>, which are the user's saved settings.
    /// </summary>
    public static IReadOnlyList<QaIssue> Check(TranslationUnit unit, QaOptions? options = null, ITermSource? terms = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        options ??= QaOptions.Default;
        if (unit.Translate == false)
            return [];

        var issues = new List<QaIssue>();
        foreach (var qa in QaSegment.OfUnit(unit, terms).Where(s => !s.Segment.IsIgnorable && s.Segment.Target is not null))
        {
            foreach (var check in Checks.Where(c => options.IsEnabled(c.Kind)))
                issues.AddRange(check.Check(qa));
        }

        return issues;
    }
}
