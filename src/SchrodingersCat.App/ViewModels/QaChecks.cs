using SchrodingersCat.Core.Checks;

namespace SchrodingersCat.App.ViewModels;

/// <summary>The quality checks in words, for the report's groups and the settings dialog.</summary>
public static class QaChecks
{
    /// <summary>The report's group for the code check's issues, which can't be switched off.</summary>
    public const string CodesLabel = "Inline codes";

    /// <summary>Every check that can be switched off, in the order the report and the settings list them.</summary>
    public static IReadOnlyList<QaIssueKind> All { get; } =
    [
        QaIssueKind.Numbers,
        QaIssueKind.DoNotTranslate,
        QaIssueKind.Terminology,
        QaIssueKind.SameAsSource,
        QaIssueKind.Untranslated,
        QaIssueKind.RepeatedWord,
        QaIssueKind.InconsistentTranslation,
        QaIssueKind.InconsistentSource,
        QaIssueKind.Spaces,
        QaIssueKind.EndPunctuation,
        QaIssueKind.Brackets,
    ];

    public static string Label(QaIssueKind kind) => kind switch
    {
        QaIssueKind.Numbers => "Numbers",
        QaIssueKind.SameAsSource => "Target same as source",
        QaIssueKind.DoNotTranslate => "Text not to translate",
        QaIssueKind.RepeatedWord => "Repeated words",
        QaIssueKind.Spaces => "Spaces",
        QaIssueKind.EndPunctuation => "End punctuation",
        QaIssueKind.Brackets => "Brackets and quotes",
        QaIssueKind.Terminology => "Terminology",
        QaIssueKind.Untranslated => "Untranslated segments",
        QaIssueKind.InconsistentTranslation => "Inconsistent translations",
        QaIssueKind.InconsistentSource => "Inconsistent sources",
        _ => kind.ToString(),
    };
}
