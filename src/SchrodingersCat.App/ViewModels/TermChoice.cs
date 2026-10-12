using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One translation in the term picker (Ctrl+T, section 6 of <c>docs/design/termbase.md</c>).</summary>
/// <param name="Text">The translation, inserted as text.</param>
/// <param name="Source">The source terms it translates.</param>
/// <param name="Description">The entry's translations and notes, for the tooltip.</param>
public sealed record TermChoice(string Text, string Source, string Description)
{
    /// <summary>Type-ahead: the translation or the source term contains the typed text.</summary>
    public bool Matches(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return filter.Length == 0
            || Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Source.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The translations that may be inserted for these hits: in the hits' order, each entry's allowed non-blank
    /// translations, preferred first. Forbidden translations are never offered; a text offered already isn't again.
    /// </summary>
    public static IReadOnlyList<TermChoice> For(IEnumerable<TermHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var choices = new List<TermChoice>();
        foreach (var hit in hits)
        {
            var source = string.Join(", ", hit.MatchedTerms.Select(i => hit.Entry.Source[i].Text).Distinct(StringComparer.Ordinal));
            var description = string.Join("\n", TermTips.Ordered(hit.Entry).Select(t => t.IsForbidden ? $"{t.Text} (forbidden)" : t.Text)
                .Append(TermTips.Notes(hit.Entry) ?? "").Where(l => l.Length > 0));
            foreach (var translation in TermTips.Ordered(hit.Entry).Where(t => !t.IsForbidden))
            {
                if (seen.Add(translation.Text))
                    choices.Add(new TermChoice(translation.Text, source, description));
            }
        }

        return choices;
    }
}
