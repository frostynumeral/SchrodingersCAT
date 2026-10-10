namespace SchrodingersCat.Core.Text;

/// <summary>
/// A text tokenised and folded once for the term rule (<see cref="Words.FindAll"/>), however many terms are looked for
/// in it. Each part is worked out the first time it's needed; two threads may both work it out, with the same result.
/// </summary>
internal sealed class PreparedText
{
    private PreparedText(string text) => Text = text;

    public static PreparedText Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PreparedText(text);
    }

    public string Text { get; }

    /// <summary>The text's words, with runs of scripts without spaces split from the words around them.</summary>
    public IReadOnlyList<WordToken> Words => field ??= global::SchrodingersCat.Core.Text.Words.WordsAndRuns(Text);

    /// <summary>The text folded per character (<see cref="Words.FoldOne"/>).</summary>
    public string Folded => Folding.Folded;

    /// <summary>For each character of <see cref="Folded"/>, the index of the text's character it came from.</summary>
    public IReadOnlyList<int> Origin => Folding.Origin;

    // One object, so a thread sees both halves of the same folding.
    private Fold Folding => field ??= new Fold(global::SchrodingersCat.Core.Text.Words.FoldWithOrigin(Text));

    private sealed class Fold((string Folded, int[] Origin) folding)
    {
        public string Folded { get; } = folding.Folded;

        public int[] Origin { get; } = folding.Origin;
    }
}
