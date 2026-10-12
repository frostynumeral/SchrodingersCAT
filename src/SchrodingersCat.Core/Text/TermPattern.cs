namespace SchrodingersCat.Core.Text;

/// <summary>Which part of the term rule finds a term (<see cref="Words.FindTerm"/>).</summary>
internal enum TermRule
{
    /// <summary>Nothing finds it: the term is blank, or folds to nothing.</summary>
    None,

    /// <summary>By its folded words, each matching a word's beginning.</summary>
    Words,

    /// <summary>A term with punctuation or symbols, by its characters as written.</summary>
    Punctuated,

    /// <summary>A term in a script without spaces, or with no words, as a folded substring.</summary>
    Folded,
}

/// <summary>
/// A term classified once for the term rule (<see cref="Words.FindAll"/>), so it can be looked for in many texts, and
/// the key the termbase's index files it under (section 2 of <c>docs/design/termbase.md</c>).
/// </summary>
internal sealed class TermPattern
{
    private TermPattern(string term)
    {
        Term = term;
        (Rule, Phrase, Needle, EndsInPunctuation) = Words.Classify(term);
        Key = Rule switch
        {
            TermRule.Words => KeyOf(Words.WordRun(Phrase[0], 0), Phrase[0]),
            TermRule.Punctuated => PunctuatedKey(Needle),
            TermRule.Folded => Needle[..1],
            _ => "",
        };
    }

    public static TermPattern Of(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return new TermPattern(term);
    }

    public string Term { get; }

    public TermRule Rule { get; }

    /// <summary>The term's folded words (<see cref="TermRule.Words"/>).</summary>
    public IReadOnlyList<string> Phrase { get; }

    /// <summary>The folded term looked for (<see cref="TermRule.Punctuated"/>, <see cref="TermRule.Folded"/>).</summary>
    public string Needle { get; }

    /// <summary>Whether a word boundary must follow the term (<see cref="TermRule.Punctuated"/>).</summary>
    public bool EndsInPunctuation { get; }

    /// <summary>
    /// What any text the term is in has where it starts, folded: the first word run (<see cref="Words.WordRun"/>) of a
    /// term with words; the needle up to its first space of a term with punctuation (<c>c#</c>, <c>.net</c>,
    /// <c>wi-fi</c>), apostrophes and hyphens as one, code marks in the text skipped; the first folded character of a
    /// term in a script without spaces, or with no words; "" for <see cref="TermRule.None"/>. Cut at
    /// <see cref="Words.MaxKeyLength"/> characters.
    /// </summary>
    public string Key { get; }

    private static string PunctuatedKey(string needle)
    {
        var space = needle.IndexOf(' ', StringComparison.Ordinal);
        var length = Math.Min(space < 0 ? needle.Length : space, Words.MaxKeyLength);
        return length == needle.Length ? needle : needle[..length];
    }

    // Letters always fold to word characters, so a run is never empty; if one were, the first character still keys it.
    // A run that is the whole folded word is that string, so the key costs nothing more to keep.
    private static string KeyOf(string run, string folded) => run.Length == 0 ? folded[..1] : run == folded ? folded : run;
}
