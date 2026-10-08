using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SchrodingersCat.Core.Text;

namespace SchrodingersCat.Core.Checks;

/// <summary>Numbers in the source must be in the target, and the target has no others (section 2 of the QA design).</summary>
/// <remarks>
/// Numbers are compared by value, with <c>.</c> and <c>,</c> taken as either the decimal or the group separator and
/// spaces as group separators (decision 2): <c>1,000.5</c> and <c>1 000,5</c> are equal, <c>12.5</c> and <c>125</c>
/// aren't. A group separator must be followed by three digits; a number that fits no reading (a version, <c>1.2.3</c>)
/// is compared as written. Numbers inside <c>translate="no"</c> text are that check's business.
/// </remarks>
internal sealed partial class NumbersCheck : IQaCheck
{
    public QaIssueKind Kind => QaIssueKind.Numbers;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        var protectedTexts = segment.DoNotTranslateTexts.ToList();
        var source = Find(Mask(segment.Source.Text, segment.DoNotTranslate));
        var target = Find(MaskTexts(segment.Target.Text, protectedTexts));

        var unmatched = target.ToList();
        foreach (var number in source)
        {
            var match = unmatched.FindIndex(t => t.Values.Overlaps(number.Values));
            if (match >= 0)
            {
                unmatched.RemoveAt(match);
                continue;
            }

            yield return new QaIssue(Kind, IssueSeverity.Warning, segment.Segment, $"The number {number.Text} isn't in the target.")
            {
                Span = segment.Source.SpanOf(number.Start, number.Text.Length),
                SpanInSource = true,
            };
        }

        foreach (var extra in unmatched)
        {
            yield return new QaIssue(Kind, IssueSeverity.Warning, segment.Segment, $"The target has the number {extra.Text}, which the source doesn't.")
            {
                Span = segment.Target.SpanOf(extra.Start, extra.Text.Length),
            };
        }
    }

    private sealed record Number(int Start, string Text, HashSet<string> Values);

    // Digits, with separators between groups: a space (any width) only before exactly three digits.
    [GeneratedRegex(@"(?<![\p{L}\p{M}\p{N}_])\p{Nd}+(?:[    '][\p{Nd}]{3}(?!\p{Nd})|[.,]\p{Nd}+)*(?![\p{L}\p{M}\p{N}_])")]
    private static partial Regex Numbers();

    private static List<Number> Find(string text) =>
        [.. Numbers().Matches(text).Select(m => new Number(m.Index, m.Value, Values(m.Value)))];

    // The values a number can mean: all separators grouping, or the last one (a dot or comma) the decimal point.
    // None of them valid: the number as written, so 1.2.3 equals only 1.2.3.
    internal static HashSet<string> Values(string number)
    {
        var groups = new List<string>();
        var separators = new List<char>();
        var digits = new StringBuilder();
        foreach (var c in number)
        {
            if (CharUnicodeInfo.GetDecimalDigitValue(c) is var value and >= 0)
            {
                digits.Append((char)('0' + value));
                continue;
            }

            groups.Add(digits.ToString());
            separators.Add(c);
            digits.Clear();
        }

        groups.Add(digits.ToString());

        var values = new HashSet<string>(StringComparer.Ordinal);
        if (groups.Skip(1).All(g => g.Length == 3))
            values.Add(Canonical(string.Concat(groups), ""));
        if (separators.Count > 0 && separators[^1] is '.' or ',' && groups.Skip(1).SkipLast(1).All(g => g.Length == 3))
            values.Add(Canonical(string.Concat(groups.SkipLast(1)), groups[^1]));
        if (values.Count == 0)
            values.Add(number);
        return values;
    }

    private static string Canonical(string whole, string fraction)
    {
        whole = whole.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        return (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? "" : "." + fraction);
    }

    private static string Mask(string text, IReadOnlyList<(int Start, int Length)> ranges)
    {
        var masked = text.ToCharArray();
        foreach (var (start, length) in ranges)
            Array.Fill(masked, ' ', start, length);
        return new string(masked);
    }

    private static string MaskTexts(string text, List<string> texts)
    {
        foreach (var protectedText in texts)
            text = text.Replace(protectedText, new string(' ', protectedText.Length), StringComparison.Ordinal);
        return text;
    }
}

/// <summary>The target is the source copied over (section 2): only when the source has some letters to translate.</summary>
internal sealed class SameAsSourceCheck : IQaCheck
{
    /// <summary>Sources with fewer letters (<c>OK</c>, <c>{0}</c>, <c>42</c>) are often the same in any language.</summary>
    public const int MinimumLetters = 3;

    public QaIssueKind Kind => QaIssueKind.SameAsSource;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        var source = segment.Source.Text.Trim();
        if (source.Count(char.IsLetter) >= MinimumLetters && string.Equals(source, segment.Target.Text.Trim(), StringComparison.Ordinal))
            yield return new QaIssue(Kind, IssueSeverity.Warning, segment.Segment, "The target is the same as the source.");
    }
}

/// <summary>Text marked <c>translate="no"</c> in the source must be in the target unchanged (section 2).</summary>
internal sealed class DoNotTranslateCheck : IQaCheck
{
    public QaIssueKind Kind => QaIssueKind.DoNotTranslate;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        var target = segment.Target.Text.Replace(QaText.CodeMark.ToString(), "", StringComparison.Ordinal);
        foreach (var (start, length) in segment.DoNotTranslate)
        {
            var text = segment.Source.Text.Substring(start, length).Replace(QaText.CodeMark.ToString(), "", StringComparison.Ordinal).Trim();
            if (text.Length > 0 && !target.Contains(text, StringComparison.Ordinal))
            {
                yield return new QaIssue(Kind, IssueSeverity.Warning, segment.Segment, $"“{text}” is marked not to be translated, but the target doesn't have it unchanged.")
                {
                    Span = segment.Source.SpanOf(start, length),
                    SpanInSource = true,
                };
            }
        }
    }
}

/// <summary>The same word twice in a row in the target (<c>the the</c>), unless the source repeats it too (section 2).</summary>
internal sealed class RepeatedWordCheck : IQaCheck
{
    public QaIssueKind Kind => QaIssueKind.RepeatedWord;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        var sourceRepeats = Repeats(segment.Source.Text).Select(r => r.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (start, length, word) in Repeats(segment.Target.Text))
        {
            if (sourceRepeats.Contains(word))
                continue;

            var text = segment.Target.Text.Substring(start, length);
            yield return new QaIssue(Kind, IssueSeverity.Warning, segment.Segment, $"“{text}”: a word is repeated.")
            {
                Span = segment.Target.SpanOf(start, length),
            };
        }
    }

    // Two words with letters, equal ignoring case, with only whitespace between.
    private static IEnumerable<(int Start, int Length, string Word)> Repeats(string text)
    {
        Match? previous = null;
        foreach (Match token in Words.Tokens().Matches(text))
        {
            var isWord = !Words.IsPunctuation(token.Value) && token.Value.Any(char.IsLetter);
            if (isWord && previous is not null
                && string.Equals(previous.Value, token.Value, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(text[(previous.Index + previous.Length)..token.Index]))
                yield return (previous.Index, token.Index + token.Length - previous.Index, token.Value);

            previous = isWord ? token : null;
        }
    }
}

/// <summary>A double space the source doesn't have, or other whitespace at either end than the source's (section 2).</summary>
internal sealed class SpacesCheck : IQaCheck
{
    public QaIssueKind Kind => QaIssueKind.Spaces;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        string source = segment.Source.Text, target = segment.Target.Text;

        var doubled = target.IndexOf("  ", StringComparison.Ordinal);
        if (doubled >= 0 && !source.Contains("  ", StringComparison.Ordinal))
            yield return new QaIssue(Kind, IssueSeverity.Info, segment.Segment, "The target has a double space.") { Span = segment.Target.SpanOf(doubled, 2) };

        if (Leading(source) != Leading(target))
            yield return new QaIssue(Kind, IssueSeverity.Info, segment.Segment, $"The target starts with {Describe(Leading(target))}, the source with {Describe(Leading(source))}.");
        if (Trailing(source) != Trailing(target))
            yield return new QaIssue(Kind, IssueSeverity.Info, segment.Segment, $"The target ends with {Describe(Trailing(target))}, the source with {Describe(Trailing(source))}.");
    }

    private static string Leading(string text) => text[..(text.Length - text.TrimStart().Length)];

    private static string Trailing(string text) => text[text.TrimEnd().Length..];

    private static string Describe(string whitespace) => whitespace switch
    {
        "" => "no whitespace",
        " " => "a space",
        _ when whitespace.All(c => c == ' ') => $"{whitespace.Length} spaces",
        _ => "whitespace",
    };
}

/// <summary>The target ends with other punctuation than the source's <c>.</c>, <c>?</c>, <c>!</c> or <c>:</c> (section 2).</summary>
internal sealed class EndPunctuationCheck : IQaCheck
{
    // What may stand for each in other scripts: CJK full-width marks, the ellipsis, the Greek question mark (which is
    // also written as a semicolon), the Arabic question mark, the Devanagari danda.
    private static readonly Dictionary<char, string> Equivalents = new()
    {
        ['.'] = ".。．…।",
        ['?'] = "?？;\u037E؟",
        ['!'] = "!！",
        [':'] = ":：",
    };

    // Closing quotes and brackets can follow the punctuation: "Go." ends with a full stop.
    private const string Closers = "\"'”’»›)]}」』）" + "￼";

    public QaIssueKind Kind => QaIssueKind.EndPunctuation;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        if (Last(segment.Source.Text) is not { } source || !Equivalents.TryGetValue(source, out var allowed))
            yield break;

        var target = Last(segment.Target.Text);
        if (target is { } t && allowed.Contains(t, StringComparison.Ordinal))
            yield break;

        var what = target is null || char.IsLetterOrDigit(target.Value) ? "no punctuation" : $"“{target}”";
        yield return new QaIssue(Kind, IssueSeverity.Info, segment.Segment, $"The source ends with “{source}”, the target with {what}.");
    }

    private static char? Last(string text)
    {
        var trimmed = text.TrimEnd().TrimEnd(Closers.ToCharArray()).TrimEnd();
        return trimmed.Length == 0 ? null : trimmed[^1];
    }
}

/// <summary>A bracket or quote without its other half in the target, when the source has them all paired (section 2).</summary>
internal sealed class BracketsCheck : IQaCheck
{
    private const string Opening = "([{「『《〈（";
    private const string Closing = ")]}」』》〉）";

    // Quotes whose opening and closing marks differ by language (“…” in English, „…“ in German, „…” in Hungarian):
    // each family must just come in pairs.
    private static readonly string[] QuoteFamilies = ["\"", "“”„‟", "«»", "‹›"];

    public QaIssueKind Kind => QaIssueKind.Brackets;

    public IEnumerable<QaIssue> Check(QaSegment segment)
    {
        if (Unpaired(segment.Source.Text) is not null)
            yield break;

        if (Unpaired(segment.Target.Text) is { } at)
        {
            yield return new QaIssue(Kind, IssueSeverity.Info, segment.Segment, $"The target has a “{segment.Target.Text[at]}” without its other half.")
            {
                Span = segment.Target.SpanOf(at, 1),
            };
        }
    }

    // Where the first unpaired bracket or quote is; null if all are paired.
    private static int? Unpaired(string text)
    {
        var open = new Stack<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (Opening.Contains(text[i], StringComparison.Ordinal))
                open.Push(i);
            else if (Closing.IndexOf(text[i], StringComparison.Ordinal) is var kind and >= 0)
            {
                if (open.Count == 0 || Opening[kind] != text[open.Peek()])
                    return i;
                open.Pop();
            }
        }

        if (open.Count > 0)
            return open.Last();

        foreach (var family in QuoteFamilies)
        {
            var marks = Enumerable.Range(0, text.Length).Where(i => family.Contains(text[i], StringComparison.Ordinal)).ToList();
            if (marks.Count % 2 == 1)
                return marks[^1];
        }

        return null;
    }
}
