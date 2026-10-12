using System.Windows.Input;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One translation in the terms list: forbidden ones are struck through and never inserted.</summary>
public sealed record TermTranslation(string Text, TermStatus Status, string? Note)
{
    public bool IsForbidden => Status == TermStatus.Forbidden;

    public bool IsPreferred => Status == TermStatus.Preferred;

    /// <summary>What a screen reader says, so the strikethrough isn't the only sign of a forbidden translation.</summary>
    public string AccessibleName => IsForbidden ? $"forbidden: {Text}" : IsPreferred ? $"preferred: {Text}" : Text;
}

/// <summary>
/// A row of the terms list (section 6 of <c>docs/design/termbase.md</c>): an entry found in the selected segment's
/// source, with its translations and notes, and an Insert button for its first allowed translation.
/// </summary>
public sealed class TermViewModel
{
    /// <param name="insert">Inserts the row's <see cref="FirstAllowed"/> translation; its parameter is the row.</param>
    public TermViewModel(TermHit hit, ICommand insert)
    {
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));
        InsertCommand = insert ?? throw new ArgumentNullException(nameof(insert));
        SourceText = string.Join(", ", hit.MatchedTerms.Select(i => hit.Entry.Source[i].Text).Distinct(StringComparer.Ordinal));
        Translations = TermTips.Ordered(hit.Entry);
        FirstAllowed = Translations.FirstOrDefault(t => !t.IsForbidden)?.Text;
        Notes = TermTips.Notes(hit.Entry);
    }

    public TermHit Hit { get; }

    /// <summary>The source terms found (synonyms of one entry can all be in the source).</summary>
    public string SourceText { get; }

    /// <summary>The non-blank translations: preferred first, then the others in stored order, forbidden ones last.</summary>
    public IReadOnlyList<TermTranslation> Translations { get; }

    /// <summary>What Insert puts in the target; null if the entry has no translation that may be used.</summary>
    public string? FirstAllowed { get; }

    /// <summary>The entry's and its terms' notes, for the row's tooltip; null if there are none.</summary>
    public string? Notes { get; }

    public ICommand InsertCommand { get; }
}

/// <summary>What the terms list, the marks' tooltips and the term picker say about an entry.</summary>
internal static class TermTips
{
    /// <summary>The entry's non-blank translations: preferred first, then the others in stored order, forbidden ones last.</summary>
    public static IReadOnlyList<TermTranslation> Ordered(TermEntry entry) =>
        [.. entry.Target
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .OrderBy(t => t.Status switch { TermStatus.Preferred => 0, TermStatus.Forbidden => 2, _ => 1 })
            .Select(t => new TermTranslation(t.Text, t.Status, t.Note))];

    /// <summary>The entry's note, then each term's (<c>term: note</c>); null if there are none.</summary>
    public static string? Notes(TermEntry entry)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Note))
            notes.Add(entry.Note);
        notes.AddRange(entry.Source.Concat(entry.Target).Where(t => !string.IsNullOrWhiteSpace(t.Note)).Select(t => $"{t.Text}: {t.Note}"));
        return notes.Count == 0 ? null : string.Join("\n", notes);
    }

    /// <summary>
    /// The tooltip of a term marked in the source: the termbase and the source terms found, the allowed translations,
    /// the forbidden ones, and the notes.
    /// </summary>
    public static string Of(TermHit hit, string termbaseName)
    {
        var translations = Ordered(hit.Entry);
        var lines = new List<string>
        {
            $"Termbase “{termbaseName}”: {string.Join(", ", hit.MatchedTerms.Select(i => hit.Entry.Source[i].Text).Distinct(StringComparer.Ordinal))}",
            translations.Any(t => !t.IsForbidden)
                ? "→ " + string.Join(", ", translations.Where(t => !t.IsForbidden).Select(t => t.Text))
                : "No translation yet.",
        };
        if (translations.Where(t => t.IsForbidden).Select(t => t.Text).ToList() is { Count: > 0 } forbidden)
            lines.Add("Forbidden: " + string.Join(", ", forbidden));
        if (Notes(hit.Entry) is { } notes)
            lines.Add(notes);
        return string.Join("\n", lines);
    }
}
