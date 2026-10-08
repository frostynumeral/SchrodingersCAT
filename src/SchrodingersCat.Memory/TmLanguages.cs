namespace SchrodingersCat.Memory;

/// <summary>
/// Whether a TM serves a document's languages (decision 3 of <c>docs/design/translation-memory.md</c>): a TM serves its
/// own languages, and their regional variants at a 1-point penalty (a <c>de</c> TM serves a <c>de-AT</c> document); a
/// regional TM serves only its own region (a <c>de-AT</c> TM doesn't serve <c>de</c> or <c>de-DE</c>).
/// </summary>
public static class TmLanguages
{
    /// <summary>What a match from a TM for a regional variant's base language loses.</summary>
    public const int VariantPenalty = 1;

    /// <summary>
    /// The points every match loses: 0 for the same languages (ignoring case), <see cref="VariantPenalty"/> if either
    /// document language is a regional variant of the TM's; null if the TM doesn't serve the document. A document
    /// without a target language yet is checked by its source language only.
    /// </summary>
    public static int? Penalty(string tmSource, string tmTarget, string documentSource, string? documentTarget)
    {
        ArgumentNullException.ThrowIfNull(tmSource);
        ArgumentNullException.ThrowIfNull(tmTarget);
        ArgumentNullException.ThrowIfNull(documentSource);

        var source = Fit(tmSource, documentSource);
        var target = documentTarget is null ? Kind.Same : Fit(tmTarget, documentTarget);
        if (source == Kind.None || target == Kind.None)
            return null;
        return source == Kind.Variant || target == Kind.Variant ? VariantPenalty : 0;
    }

    /// <summary>Whether text in <paramref name="language"/> serves <paramref name="wanted"/>: the same, or a regional variant of it.</summary>
    internal static bool Serves(string language, string wanted) => Fit(wanted, language) != Kind.None;

    private enum Kind
    {
        None,
        Same,
        Variant,
    }

    private static Kind Fit(string tm, string document) =>
        string.Equals(tm, document, StringComparison.OrdinalIgnoreCase) ? Kind.Same
        : document.StartsWith(tm + "-", StringComparison.OrdinalIgnoreCase) ? Kind.Variant
        : Kind.None;
}
