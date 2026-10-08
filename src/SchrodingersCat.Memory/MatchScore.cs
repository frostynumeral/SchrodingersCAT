using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>How similar two sources are, as a match percentage (design section 2).</summary>
/// <remarks>
/// <para>Sources are compared normalised: Unicode NFC, whitespace ignored between words, annotations ignored, and each
/// code as its kind only, so the same sentence with other code ids compares equal.</para>
/// <para>The score is the word-level edit distance as a percentage of the longer source. Punctuation marks are words;
/// two numbers count as the same word, so a match that differs only in numbers stays useful. Then penalties: a point
/// per number that differs, and two per code that differs (a code missing, extra, or in another order). Only sources
/// that are equal before normalisation score 100; a difference in case or whitespace alone scores 99.</para>
/// </remarks>
public static class MatchScore
{
    /// <summary>The same source.</summary>
    public const int Exact = 100;

    /// <summary>The same source, between the same neighbouring segments.</summary>
    public const int Context = 101;

    private const int NumberPenalty = 1;
    private const int CodePenalty = 2;

    /// <summary>The match percentage of <paramref name="candidate"/> for <paramref name="source"/>, 0–100. Symmetric.</summary>
    public static int Compare(InlineContent source, InlineContent candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);

        return Compare(Normalized.Of(source), Normalized.Of(candidate));
    }

    /// <summary>Whether two neighbouring sources count as the same context: equal once whitespace is normalised.</summary>
    public static bool SameContext(string? a, string? b) => NormalizeSpace(a) == NormalizeSpace(b);

    internal static int Compare(Normalized a, Normalized b)
    {
        if (a.Key == b.Key)
            return Exact;

        var longer = Math.Max(a.Words.Length, b.Words.Length);
        var distance = WordDistance(a.Words, b.Words);
        var score = longer == 0 ? Exact : Exact * (longer - distance) / longer;
        score -= NumberPenalty * MultisetDifference(a.Numbers, b.Numbers);
        score -= CodePenalty * CodeDifference(a.Codes, b.Codes);
        return Math.Clamp(score, 0, Exact - 1);
    }

    /// <summary>A source prepared for comparing; computed once per TM entry.</summary>
    internal sealed record Normalized(string Key, Word[] Words, string[] Numbers, InlineCodeKind[] Codes)
    {
        public static Normalized Of(InlineContent content)
        {
            var key = new StringBuilder();
            var words = new List<Word>();
            var codes = new List<InlineCodeKind>();
            foreach (var element in content)
            {
                switch (element)
                {
                    case InlineText text:
                        var normalized = Nfc(text.Text);
                        key.Append(normalized);
                        foreach (Match match in Core.Text.Words.Tokens().Matches(normalized))
                            words.Add(new Word(match.Value.ToLower(CultureInfo.InvariantCulture), match.Groups["number"].Success));
                        break;
                    case InlineCode code:
                        key.Append('').Append((int)code.Kind);
                        codes.Add(code.Kind);
                        break;
                }
            }

            return new Normalized(key.ToString(), [.. words], [.. words.Where(w => w.IsNumber).Select(w => w.Text)], [.. codes]);
        }

        /// <summary>Only the <see cref="Key"/>, without tokenizing (several times faster than <see cref="Of"/>).</summary>
        public static string KeyOf(InlineContent content)
        {
            var key = new StringBuilder();
            foreach (var element in content)
            {
                if (element is InlineText text)
                    key.Append(Nfc(text.Text));
                else if (element is InlineCode code)
                    key.Append('\uE000').Append((int)code.Kind);
            }

            return key.ToString();
        }
    }

    internal readonly record struct Word(string Text, bool IsNumber);

    // Insertions and deletions cost 1, a substitution 1 unless the words are equal (ignoring case) or both numbers.
    private static int WordDistance(Word[] a, Word[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var same = a[i - 1].Text == b[j - 1].Text || (a[i - 1].IsNumber && b[j - 1].IsNumber);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (same ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // The larger of the two one-sided differences, so the result is symmetric.
    private static int MultisetDifference<T>(T[] a, T[] b)
        where T : notnull
    {
        var counts = new Dictionary<T, int>();
        foreach (var item in a)
            counts[item] = counts.GetValueOrDefault(item) + 1;
        foreach (var item in b)
            counts[item] = counts.GetValueOrDefault(item) - 1;
        return Math.Max(counts.Values.Where(c => c > 0).Sum(), -counts.Values.Where(c => c < 0).Sum());
    }

    // Codes missing or extra; if there are none, one for a different order.
    private static int CodeDifference(InlineCodeKind[] a, InlineCodeKind[] b)
    {
        var difference = MultisetDifference(a, b);
        return difference == 0 && !a.SequenceEqual(b) ? 1 : difference;
    }

    private static string NormalizeSpace(string? text) =>
        string.Join(' ', Nfc(text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Content can hold code points Normalize rejects (a lone surrogate, U+FFFF, from <cp>); such text is compared as it is.
    internal static string Nfc(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
