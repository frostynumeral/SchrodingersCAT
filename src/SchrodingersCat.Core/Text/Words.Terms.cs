using System.Globalization;
using System.Text;

namespace SchrodingersCat.Core.Text;

// The term rule (section 2 of docs/design/termbase.md): how the QA check, and the termbase's index, find a term in a
// text. A term is classified once (TermPattern), a text prepared once (PreparedText), so a lookup with many terms
// tokenises and folds its text only once.
internal static partial class Words
{
    /// <summary>How many characters of a term's first word run key it in the termbase's index (<see cref="WordRun"/>).</summary>
    internal const int MaxKeyLength = 16;

    /// <summary>
    /// Where a term (or a translation) first is in a text, as a range of the text; null if it isn't there. Words match
    /// by their beginning, folded (<see cref="FindPhrase"/>), so <c>fájl</c> finds <c>fájlt</c>. In scripts written
    /// without spaces (Chinese, Japanese, Thai, …) a "word" is a whole clause, so a term in such a script, and a term
    /// with no words (only symbols), is found as a folded substring instead, starting and ending where characters of
    /// the text do (<c>カ</c> isn't in <c>ガ</c>, <c>をs</c> isn't in <c>をß</c>; a Latin <c>s</c> takes the word rule, so
    /// it is found in <c>ß</c>); and in the text, runs of such a script are
    /// kept apart from the words around them, so <c>このprofileを開く</c> has the word <c>profile</c>, not <c>file</c>.
    /// A term with punctuation or symbols (<c>C#</c>, <c>.NET</c>, <c>Wi-Fi</c>) is found only where the text has the
    /// same characters (<see cref="FindPunctuated"/>).
    /// </summary>
    internal static (int Start, int Length)? FindTerm(string text, string term)
    {
        foreach (var range in FindAll(PreparedText.Of(text), TermPattern.Of(term)))
            return range;
        return null;
    }

    /// <summary>
    /// Every place the term is in the text, by the rule of <see cref="FindTerm"/>: ascending by start, distinct ranges,
    /// overlapping ones included (<c>ああ</c> is twice in <c>あああ</c>).
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> FindAll(PreparedText text, TermPattern term)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(term);

        return term.Rule switch
        {
            TermRule.Words => FindWords(text, term.Phrase),
            TermRule.Punctuated => FindPunctuated(text, term.Needle, term.EndsInPunctuation),
            TermRule.Folded => FindFolded(text, term.Needle),
            _ => [],
        };
    }

    // Which rule finds a term, with what it needs, in the order FindTerm has always tried them: a term with no words
    // or in a script without spaces as a substring, one with punctuation as written, the others by their words.
    internal static (TermRule Rule, IReadOnlyList<string> Phrase, string Needle, bool EndsInPunctuation) Classify(string term)
    {
        var termWords = Find(term).Select(w => w.Folded).ToList();
        if (termWords.Count == 0 || NoSpaces().IsMatch(term))
        {
            var needle = Fold(term.Trim());
            return (needle.Length == 0 ? TermRule.None : TermRule.Folded, [], needle, false);
        }

        if (Tokens().Matches(term).Any(t => IsPunctuation(t.Value)))
        {
            // Whitespace in the needle is a single space, which stands for a run of whitespace in the text.
            var needle = string.Join(' ', Fold(term).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => string.Concat(word.Select(Variant))));
            var trimmed = term.TrimEnd();
            var endsInPunctuation = trimmed.Length > 0 && !IsWordChar(trimmed, trimmed.Length - 1);
            return (TermRule.Punctuated, [], needle, endsInPunctuation);
        }

        return (TermRule.Words, termWords, "", false);
    }

    /// <summary>
    /// The run of key characters (<see cref="IsKeyChar"/>) from a place in folded text, with code marks after its first
    /// character skipped, cut at <see cref="MaxKeyLength"/>: "" if the character there isn't one. A term's key and the
    /// keys a text is probed with are both cut by this, so they agree (section 2 of <c>docs/design/termbase.md</c>).
    /// </summary>
    internal static string WordRun(string folded, int start)
    {
        Span<char> run = stackalloc char[MaxKeyLength];
        var length = 0;
        for (var i = start; i < folded.Length && length < MaxKeyLength; i++)
        {
            if (IsKeyChar(folded[i]))
                run[length++] = folded[i];
            else if (i == start || folded[i] != Checks.QaText.CodeMark)
                break;
        }

        return run[..length].ToString();
    }

    /// <summary>A UTF-16 unit that makes up words in folded text: a letter, mark, number or underscore (not a surrogate).</summary>
    internal static bool IsKeyChar(char c) => c == '_' || IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(c));

    // Every place the phrase starts among the text's words (and runs of scripts without spaces), from the first
    // word's start to the last word's end.
    private static IEnumerable<(int Start, int Length)> FindWords(PreparedText text, IReadOnlyList<string> phrase)
    {
        var words = text.Words;
        for (var i = 0; i + phrase.Count <= words.Count; i++)
        {
            if (!PhraseAt(words, phrase, i))
                continue;

            var last = words[i + phrase.Count - 1];
            yield return (words[i].Start, last.Start + last.Length - words[i].Start);
        }
    }

    // The text's words, each split where it changes between a script without spaces and any other.
    internal static List<WordToken> WordsAndRuns(string text)
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

    // The term as a folded substring of the text, starting where a character of the text starts and ending where
    // one ends: not in the middle of ß's "ss", and not before a kana voicing mark Fold keeps, as カ (ka) isn't ガ (ga),
    // whether ガ is one character or カ and a combining mark.
    private static IEnumerable<(int Start, int Length)> FindFolded(PreparedText text, string needle)
    {
        var folded = text.Folded;
        var origin = text.Origin;
        for (var at = 0; at + needle.Length <= folded.Length; at++)
        {
            if (at > 0 && origin[at] == origin[at - 1] || !folded.AsSpan(at).StartsWith(needle, StringComparison.Ordinal))
                continue;

            var end = at + needle.Length;
            if (end < folded.Length && (origin[end] == origin[end - 1] || folded[end] is '゙' or '゚'))
                continue;

            yield return RangeOf(text.Text, origin, at, end);
        }
    }

    /// <summary>
    /// A term with punctuation (section 2 of <c>docs/design/termbase.md</c>): the text has the same characters, folded,
    /// where only whitespace may differ (a run of it for a run of it), so <c>Wi-Fi</c> finds <c>wi-fi</c> but not
    /// <c>Wi Fi</c>, and <c>C#</c> doesn't find <c>C</c>. Apostrophes count as one character (<c>'</c>, <c>’</c>, …), and
    /// so do hyphens (<c>-</c>, the non-breaking hyphen, …), as typing and typography differ. Codes are left out, as
    /// the word rule leaves them out: a <see cref="Checks.QaText.CodeMark"/> inside the term is skipped, and one at a
    /// gap counts as whitespace, so <c>Wi-Fi settings</c> is found in <c>⟨b⟩Wi-Fi⟨/b⟩ settings</c>. It starts at a
    /// word beginning, so <c>.NET</c> isn't in <c>ASP.NET</c>. Its last word matches by its beginning, so suffixes count
    /// (<c>e-mail</c> finds <c>e-mailt</c>, <c>Wi-Fi</c> finds <c>Wi-Fi-t</c>, <c>x-Gros</c> finds <c>x-großartig</c>); a
    /// term that ends in punctuation (<c>C#</c>, <c>C++</c>) needs a word boundary after it, so <c>C#</c> isn't in
    /// <c>C#x</c>. As elsewhere, a script without spaces next to another counts as a boundary (<c>このC#を</c>).
    /// </summary>
    private static IEnumerable<(int Start, int Length)> FindPunctuated(PreparedText prepared, string needle, bool endsInPunctuation)
    {
        var text = prepared.Text;
        var folded = prepared.Folded;
        var origin = prepared.Origin;
        for (var at = 0; at + needle.Length <= folded.Length; at++)
        {
            // Only where a character of the text starts (not in the middle of ß's "ss"), at a word beginning.
            if (at > 0 && origin[at] == origin[at - 1] || !IsBoundary(text, origin[at], origin[at] - 1))
                continue;

            // It may end in the middle of a character's fold: the last word matches by its beginning (gros in groß).
            if (MatchAt(folded, at, needle) is not { } end)
                continue;

            var range = RangeOf(text, origin, at, end);
            if (endsInPunctuation && !IsBoundary(text, range.Start + range.Length - 1, range.Start + range.Length))
                continue;

            yield return range;
        }
    }

    // Where the needle ends in the folded text if it's there at this position; null if it isn't. A space in the needle
    // takes a run of whitespace and code marks; a code mark elsewhere is skipped.
    private static int? MatchAt(string folded, int at, string needle)
    {
        var i = at;
        foreach (var c in needle)
        {
            if (c == ' ')
            {
                var gap = i;
                while (i < folded.Length && (char.IsWhiteSpace(folded[i]) || folded[i] == Checks.QaText.CodeMark))
                    i++;
                if (i == gap)
                    return null;
                continue;
            }

            while (i > at && i < folded.Length && folded[i] == Checks.QaText.CodeMark)
                i++;
            if (i >= folded.Length || Variant(folded[i]) != c)
                return null;
            i++;
        }

        return i;
    }

    // Apostrophes as one character, and hyphens as one.
    private static char Variant(char c) => c switch
    {
        '‘' or '’' or '＇' => '\'',
        '‐' or '‑' or '﹣' or '－' => '-',
        _ => c,
    };

    // The text folded per character, and for each folded character the index of the character it came from.
    internal static (string Folded, int[] Origin) FoldWithOrigin(string text)
    {
        var folded = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var piece = FoldOne(text[i]);
            folded.Append(piece);
            for (var j = 0; j < piece.Length; j++)
                origin.Add(i);
        }

        return (folded.ToString(), [.. origin]);
    }

    // A range of folded characters (end exclusive, not empty) as a range of the text, taking in combining marks that
    // fold to nothing after its end, so a highlight never splits a letter from its mark.
    private static (int Start, int Length) RangeOf(string text, IReadOnlyList<int> origin, int start, int end)
    {
        var from = origin[start];
        var to = origin[end - 1] + 1;
        while (to < text.Length && FoldOne(text[to]).Length == 0)
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
}
