using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>How a piece of a match's source compares with the current source.</summary>
public enum DiffKind
{
    /// <summary>In both.</summary>
    Same,

    /// <summary>In the match's source, not in the current source: the match's translation has it, and shouldn't.</summary>
    Removed,

    /// <summary>In the current source, not in the match's source: the match's translation lacks it.</summary>
    Added,
}

/// <summary>A piece of a <see cref="SourceDiff"/>: text (words with the whitespace around them) or a code half.</summary>
public sealed record DiffPart(InlineElement Element, DiffKind Kind);

/// <summary>
/// What differs between a match's source and the current source, so the matches pane can highlight it (design
/// section 6): the match's source, with the current source's words that it lacks inserted where they belong.
/// </summary>
/// <remarks>
/// The words are the ones <see cref="MatchScore"/> counts (punctuation marks are words). Unlike the score, the diff
/// shows every difference the translator has to fix in the match's translation: case, and other numbers. Codes are
/// compared by kind, as in the score, and annotations are left out. Whitespace is never a difference: the match's
/// is shown as it is, and whitespace only the current source has is left out unless it separates added words.
/// </remarks>
public static class SourceDiff
{
    /// <summary>
    /// The largest comparison (the words that differ in one source times those in the other) worked out word by word;
    /// beyond it, the differing middle is shown as removed, then added, rather than spending the UI's time on it.
    /// </summary>
    public const int MaxCells = 1_000_000;

    public static IReadOnlyList<DiffPart> Compare(InlineContent current, InlineContent matchSource)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(matchSource);

        var a = Tokenize(matchSource);
        var b = Tokenize(current);

        // Only the differing middle needs the quadratic comparison.
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix].Key == b[prefix].Key)
            prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)].Key == b[^(suffix + 1)].Key)
            suffix++;

        var parts = new List<DiffPart>();
        for (var i = 0; i < prefix; i++)
            Add(parts, a[i].Element, DiffKind.Same);
        Middle(parts, a.GetRange(prefix, a.Count - prefix - suffix), b.GetRange(prefix, b.Count - prefix - suffix));
        for (var i = a.Count - suffix; i < a.Count; i++)
            Add(parts, a[i].Element, DiffKind.Same);

        // Whitespace on its own isn't worth highlighting: the match's stays as text, the current source's goes.
        var cleaned = new List<DiffPart>();
        foreach (var part in parts)
        {
            if (part is not { Element: InlineText { Text: var text }, Kind: not DiffKind.Same } || !string.IsNullOrWhiteSpace(text))
                Add(cleaned, part.Element, part.Kind);
            else if (part.Kind == DiffKind.Removed)
                Add(cleaned, part.Element, DiffKind.Same);
        }

        return cleaned;
    }

    // A word, a run of whitespace, or a code half; Key is what two tokens must share to be the same.
    private readonly record struct Token(InlineElement Element, string Key);

    // The longest common subsequence, walked from the start: at each difference, the match's tokens first.
    private static void Middle(List<DiffPart> parts, List<Token> a, List<Token> b)
    {
        if ((long)a.Count * b.Count > MaxCells)
        {
            foreach (var token in a)
                Add(parts, token.Element, DiffKind.Removed);
            foreach (var token in b)
                Add(parts, token.Element, DiffKind.Added);
            return;
        }

        // common[i, j]: the length of the longest common subsequence of a[i..] and b[j..].
        var common = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
                common[i, j] = a[i].Key == b[j].Key ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        }

        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x].Key == b[y].Key)
            {
                Add(parts, a[x++].Element, DiffKind.Same);
                y++;
            }
            else if (y == b.Count || (x < a.Count && common[x + 1, y] >= common[x, y + 1]))
            {
                Add(parts, a[x++].Element, DiffKind.Removed);
            }
            else
            {
                Add(parts, b[y++].Element, DiffKind.Added);
            }
        }
    }

    private static List<Token> Tokenize(InlineContent content)
    {
        var tokens = new List<Token>();
        foreach (var element in content)
        {
            switch (element)
            {
                case InlineText { Text: var text }:
                    var position = 0;
                    foreach (System.Text.RegularExpressions.Match word in Core.Text.Words.Tokens().Matches(text))
                    {
                        if (word.Index > position)
                            tokens.Add(new Token(new InlineText(text[position..word.Index]), " "));
                        tokens.Add(new Token(new InlineText(word.Value), "w" + MatchScore.Nfc(word.Value)));
                        position = word.Index + word.Length;
                    }

                    if (position < text.Length)
                        tokens.Add(new Token(new InlineText(text[position..]), " "));
                    break;
                case InlineCode code:
                    tokens.Add(new Token(code, "c" + (int)code.Kind));
                    break;
            }
        }

        return tokens;
    }

    // Neighbouring text of the same kind becomes one part.
    private static void Add(List<DiffPart> parts, InlineElement element, DiffKind kind)
    {
        if (element is InlineText text && parts.Count > 0 && parts[^1] is { Element: InlineText previous } last && last.Kind == kind)
            parts[^1] = last with { Element = new InlineText(previous.Text + text.Text) };
        else
            parts.Add(new DiffPart(element, kind));
    }
}
