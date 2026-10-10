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

    /// <summary>
    /// Where a phrase (folded words) first is among words: each word starting with the phrase's word at that place,
    /// so <c>fájl</c> finds <c>fájlt</c>. Null if it isn't there, or the phrase has no words.
    /// </summary>
    internal static int? FindPhrase(IReadOnlyList<WordToken> words, IReadOnlyList<string> phrase)
    {
        if (phrase.Count == 0)
            return null;

        for (var i = 0; i + phrase.Count <= words.Count; i++)
        {
            var all = true;
            for (var j = 0; j < phrase.Count && all; j++)
                all = words[i + j].Folded.StartsWith(phrase[j], StringComparison.Ordinal);
            if (all)
                return i;
        }

        return null;
    }

    /// <summary>
    /// Where a term (or a translation) is in a text, as a range of the text; null if it isn't there. Words match by
    /// their beginning, folded (<see cref="FindPhrase"/>), so <c>fájl</c> finds <c>fájlt</c>. In scripts written
    /// without spaces (Chinese, Japanese, Thai, …) a "word" is a whole clause, so a term in such a script, and a term
    /// with no words (only symbols), is found as a folded substring instead; and in the text, runs of such a script
    /// are kept apart from the words around them, so <c>このprofileを開く</c> has the word <c>profile</c>, not <c>file</c>.
    /// A term with punctuation or symbols (<c>C#</c>, <c>.NET</c>, <c>Wi-Fi</c>) is found only where the text has the
    /// same characters (<see cref="FindPunctuated"/>).
    /// </summary>
    internal static (int Start, int Length)? FindTerm(string text, string term)
    {
        var termWords = Find(term).Select(w => w.Folded).ToList();
        if (termWords.Count == 0 || NoSpaces().IsMatch(term))
            return FindFolded(text, term);
        if (Tokens().Matches(term).Any(t => IsPunctuation(t.Value)))
            return FindPunctuated(text, term);

        var words = WordsAndRuns(text);
        if (FindPhrase(words, termWords) is not { } first)
            return null;

        var last = words[first + termWords.Count - 1];
        return (words[first].Start, last.Start + last.Length - words[first].Start);
    }

    // The text's words, each split where it changes between a script without spaces and any other.
    private static List<WordToken> WordsAndRuns(string text)
    {
        var words = new List<WordToken>();
        foreach (var word in Find(text))
        {
            var start = word.Start;
            var end = word.Start + word.Length;
            var runStart = start;
            for (var i = start + 1; i < end; i++)
            {
                // The second half of a surrogate pair belongs with the first.
                if (char.IsLowSurrogate(text[i]) || IsNoSpaces(text, i) == IsNoSpaces(text, i - 1))
                    continue;

                words.Add(new WordToken(runStart, i - runStart, Fold(text[runStart..i])));
                runStart = i;
            }

            words.Add(new WordToken(runStart, end - runStart, Fold(text[runStart..end])));
        }

        return words;
    }

    private static bool IsNoSpaces(string text, int index) =>
        !char.IsSurrogate(text[index]) ? NoSpaces().IsMatch(text.AsSpan(index, 1))
            : index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1])
                && char.ConvertToUtf32(text[index], text[index + 1]) is >= 0x20000 and <= 0x3FFFF; // CJK extensions B–H

    // The term as a folded substring of the text.
    private static (int Start, int Length)? FindFolded(string text, string term)
    {
        var needle = Fold(term.Trim());
        if (needle.Length == 0)
            return null;

        var (folded, origin) = FoldWithOrigin(text);
        var at = folded.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? null : RangeOf(text, origin, at, at + needle.Length);
    }

    /// <summary>
    /// A term with punctuation (section 2 of <c>docs/design/termbase.md</c>): the text has the same characters, folded,
    /// where only whitespace may differ (a run of it for a run of it), so <c>Wi-Fi</c> finds <c>wi-fi</c> but not
    /// <c>Wi Fi</c>, and <c>C#</c> doesn't find <c>C</c>. It starts at a word beginning, so <c>.NET</c> isn't in
    /// <c>ASP.NET</c>. Its last word matches by its beginning, so suffixes count (<c>e-mail</c> finds <c>e-mailt</c>,
    /// <c>Wi-Fi</c> finds <c>Wi-Fi-t</c>); a term that ends in punctuation (<c>C#</c>, <c>C++</c>) needs a word boundary
    /// after it, so <c>C#</c> isn't in <c>C#x</c>. As elsewhere, a script without spaces next to another counts as a
    /// boundary (<c>このC#を</c>).
    /// </summary>
    private static (int Start, int Length)? FindPunctuated(string text, string term)
    {
        // Whitespace in the needle is a single space, which stands for a run of whitespace in the text.
        var needle = string.Join(' ', Fold(term).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var trimmed = term.TrimEnd();
        var endsInPunctuation = trimmed.Length > 0 && !IsWordChar(trimmed, trimmed.Length - 1);
        var (folded, origin) = FoldWithOrigin(text);

        for (var at = 0; at + needle.Length <= folded.Length; at++)
        {
            // Only where a character of the text starts (not in the middle of ß's "ss"), at a word beginning.
            if (at > 0 && origin[at] == origin[at - 1] || !IsBoundary(text, origin[at], origin[at] - 1))
                continue;

            if (MatchAt(folded, at, needle) is not { } end || end < folded.Length && origin[end] == origin[end - 1])
                continue;

            var range = RangeOf(text, origin, at, end);
            if (endsInPunctuation && !IsBoundary(text, range.Start + range.Length - 1, range.Start + range.Length))
                continue;

            return range;
        }

        return null;
    }

    // Where the needle ends in the folded text if it's there at this position; null if it isn't.
    private static int? MatchAt(string folded, int at, string needle)
    {
        var i = at;
        foreach (var c in needle)
        {
            if (c == ' ')
            {
                if (i >= folded.Length || !char.IsWhiteSpace(folded[i]))
                    return null;
                while (i < folded.Length && char.IsWhiteSpace(folded[i]))
                    i++;
            }
            else if (i < folded.Length && folded[i] == c)
            {
                i++;
            }
            else
            {
                return null;
            }
        }

        return i;
    }

    // The text folded per character, and for each folded character the index of the character it came from.
    private static (string Folded, List<int> Origin) FoldWithOrigin(string text)
    {
        var folded = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var piece = Fold(text[i].ToString());
            folded.Append(piece);
            origin.AddRange(Enumerable.Repeat(i, piece.Length));
        }

        return (folded.ToString(), origin);
    }

    // A range of folded characters (end exclusive, not empty) as a range of the text, taking in combining marks that
    // fold to nothing after its end, so a highlight never splits a letter from its mark.
    private static (int Start, int Length) RangeOf(string text, List<int> origin, int start, int end)
    {
        var from = origin[start];
        var to = origin[end - 1] + 1;
        while (to < text.Length && Fold(text[to].ToString()).Length == 0)
            to++;
        return (from, to - from);
    }

    // Whether a term's character next to the text's (before or after it) ends a word there: the text's isn't a
    // letter, mark, number or underscore, or one of the two is in a script without spaces and the other isn't.
    private static bool IsBoundary(string text, int inside, int outside) =>
        outside < 0 || outside >= text.Length || !IsWordChar(text, outside) || IsNoSpaces(text, CharStart(text, outside)) != IsNoSpaces(text, CharStart(text, inside));

    // A letter, mark, number or underscore, as the tokenizer's words are made of; a surrogate pair as one character.
    private static bool IsWordChar(string text, int index)
    {
        index = CharStart(text, index);
        return text[index] == '_' || IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(text, index));
    }

    // The second half of a surrogate pair belongs with the first.
    private static int CharStart(string text, int index) =>
        index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]) ? index - 1 : index;

    // Scripts written without spaces between words.
    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsKatakanaPhoneticExtensions}\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}\uFF66-\uFF9F\p{IsThai}\p{IsLao}\p{IsKhmer}\p{IsMyanmar}\p{IsTibetan}]")]
    private static partial Regex NoSpaces();

    /// <summary>The tokenizer's third kind: one character that isn't a letter, mark, number or underscore.</summary>
    internal static bool IsPunctuation(string token) =>
        token.Length == 1 && token[0] != '_' && !IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(token[0]));

    private static bool IsWordCategory(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    /// <summary>
    /// Lower case, accents removed (but not the kana voicing marks), and letters that don't decompose spelled out (ł, ø,
    /// đ, æ, œ, ß). Never fails: a
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
            // Accents go; the kana voicing marks stay, as カ (ka) and ガ (ga) are different letters.
            if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark && d is not ('\u3099' or '\u309A'))
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
