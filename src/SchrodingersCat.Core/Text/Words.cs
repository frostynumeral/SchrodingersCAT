using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SchrodingersCat.Core.Text;

/// <summary>A word found in a text: where it is, and its folded form.</summary>
internal readonly record struct WordToken(int Start, int Length, string Folded);

/// <summary>
/// Words as the TM's matching, the concordance and the quality checks see them: numbers, words and punctuation
/// marks, and a folded form for finding a word whatever its case and accents.
/// </summary>
internal static partial class Words
{
    // Each UTF-16 unit folded on its own (filled in as met): the same as folding the whole text, as decomposing,
    // lower-casing and dropping marks work per character, but without normalizing every text of a scan.
    private static readonly string?[] FoldedChars = new string?[char.MaxValue + 1];

    /// <summary>A number (with decimal or thousands separators), a word, or one punctuation mark.</summary>
    [GeneratedRegex(@"(?<number>\p{Nd}+(?:[.,]\p{Nd}+)*)(?![\p{L}\p{M}_])|[\p{L}\p{M}\p{N}_]+|[^\s\p{L}\p{M}\p{N}_]")]
    internal static partial Regex Tokens();

    /// <summary>The words of a text, without punctuation, folded.</summary>
    internal static IEnumerable<WordToken> Find(string text)
    {
        foreach (Match token in Tokens().Matches(text))
        {
            if (IsPunctuation(token.Value))
                continue;

            var folded = Fold(token.Value);
            if (folded.Length > 0)
                yield return new WordToken(token.Index, token.Length, folded);
        }
    }

    /// <summary>The tokenizer's third kind: one character that isn't a letter, mark, number or underscore.</summary>
    internal static bool IsPunctuation(string token) =>
        token.Length == 1 && token[0] != '_' && CharUnicodeInfo.GetUnicodeCategory(token[0]) is not (
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber);

    /// <summary>
    /// Lower case, accents removed, and letters that don't decompose spelled out (ł, ø, đ, æ, œ, ß). Never fails: a
    /// lone surrogate or U+FFFF (from <c>&lt;cp&gt;</c>), which Normalize rejects, stays as it is.
    /// </summary>
    internal static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 0x80)
                folded.Append(char.ToLowerInvariant(c));
            else
                folded.Append(FoldedChars[c] ??= FoldChar(c));
        }

        return folded.ToString();
    }

    private static string FoldChar(char c)
    {
        if (char.IsSurrogate(c) || c == '￿' || c == '￾')
            return c.ToString();

        string decomposed;
        try
        {
            decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return c.ToString();
        }

        var folded = new StringBuilder();
        foreach (var d in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(d);
            _ = lower switch
            {
                'ł' => folded.Append('l'),
                'ø' => folded.Append('o'),
                'đ' => folded.Append('d'),
                'æ' => folded.Append("ae"),
                'œ' => folded.Append("oe"),
                'ß' => folded.Append("ss"),
                _ => folded.Append(lower),
            };
        }

        return folded.ToString();
    }
}
