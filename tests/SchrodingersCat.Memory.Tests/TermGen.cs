using CsCheck;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Text;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// Generators for the termbase's properties: texts and terms made of the same few pieces, so terms are found often,
/// with what makes the term rule hard (suffixes, folding, scripts without spaces, punctuation, codes inside words).
/// XliffGen's texts seldom repeat a word and have almost no punctuation.
/// </summary>
internal static class TermGen
{
    private static readonly string[] Pieces =
    [
        "file", "Fájl", "fájlt", "manager", "Straße", "strasse", "groß", "c", "C", "NET", "Wi", "Fi", "e", "mail",
        "profile", "a", "_",
        "Donaudampfschifffahrt", "abcdefghijklmnop", // first words around and beyond the index's key length (16)
        "1", "2.5", "1,5", // numbers
        "この", "を", "ファイル", "カ", "ガ", "が", "文件", "ไทย", // scripts without spaces
        "é", "\U0001D400", "\U00020000", // marks, and letters outside the BMP
        "%", "→", // symbols
    ];

    private static readonly string[] Separators =
        ["", " ", "  ", "\t", " ", "-", "‑", "–", "'", "’", ".", "#", "+"];

    private static readonly Gen<string> Word = Gen.OneOfConst(Pieces);

    private static readonly Gen<string> Separator = Gen.OneOfConst(Separators);

    /// <summary>One to four pieces, joined by separators.</summary>
    public static readonly Gen<string> Term =
        Gen.Select(Word, Gen.Select(Separator, Word).Array[0, 3], (first, rest) => first + string.Concat(rest.Select(r => r.Item1 + r.Item2)));

    // A part of a content: text, a separator, a standalone code (with or without equiv), half of a pair, or text with
    // a code somewhere inside (Split is where).
    private static readonly Gen<(int Kind, string Text, int Split)> Part = Gen.Frequency(
        (6, Term.Select(t => (0, t, 0))),
        (3, Separator.Select(s => (0, s, 0))),
        (1, Gen.Const((1, "", 0))),
        (1, Word.Select(w => (2, w, 0))),
        (1, Gen.Const((3, "", 0))),
        (1, Gen.Const((4, "", 0))),
        (2, Gen.Select(Term, Gen.Int[0, 100], (t, at) => (5, t, at % (t.Length + 1)))));

    /// <summary>Text with codes anywhere, inside words too: <c>&lt;ph/&gt;</c>, with or without equiv, and <c>&lt;pc&gt;</c> pairs.</summary>
    public static readonly Gen<InlineContent> Content = Part.Array[1, 8].Select(Build);

    private static readonly Gen<TermStatus> Status = Gen.OneOfConst(Enum.GetValues<TermStatus>());

    public static Gen<TermEntry> Entry(Gen<string> term) =>
        Gen.Select(Gen.Select(term, Status).Array[1, 3], Gen.Select(term, Status).Array[0, 3], (source, target) => new TermEntry(
            [.. source.Select(t => new Term(t.Item1, t.Item2))],
            [.. target.Select(t => new Term(t.Item1, t.Item2))],
            null,
            DateTimeOffset.UnixEpoch,
            null));

    /// <summary>Entries with ids 1, 2, …</summary>
    public static Gen<StoredTermEntry[]> Entries(Gen<string> term) =>
        Entry(term).Array[0, 8].Select(entries => entries.Select((e, i) => new StoredTermEntry(new TermEntryId(i + 1), e)).ToArray());

    /// <summary>Terms cut from these texts at arbitrary places (half words, lone surrogates), often from a word's start.</summary>
    public static Gen<string> CutFrom(IReadOnlyList<string> texts) =>
        Gen.Select(Gen.Int[0, texts.Count - 1], Gen.Int[0, 1000], Gen.Int[1, 30], Gen.Bool, (which, at, length, fromWord) =>
        {
            var text = texts[which];
            if (text.Length == 0)
                return "file";

            var start = at % text.Length;
            if (fromWord)
                start = Words.Tokens().Matches(text).Select(m => m.Index).Where(i => i <= start).DefaultIfEmpty(0).Max();
            return text.Substring(start, Math.Min(length, text.Length - start));
        });

    /// <summary>
    /// A few texts, and entries with generated terms and terms cut from those texts: from their text without the codes
    /// (so a term often runs across where a code is), or as the check sees them (codes as equiv text or a code mark).
    /// </summary>
    public static readonly Gen<(InlineContent[] Texts, StoredTermEntry[] Entries)> Case =
        from texts in Content.Array[1, 4]
        from entries in Entries(Gen.Frequency(
            (1, Term),
            (2, CutFrom([.. texts.Select(t => string.Concat(t.OfType<InlineText>().Select(x => x.Text)))])),
            (1, CutFrom([.. texts.Select(t => QaText.Of(t).Text)]))))
        select (texts, entries);

    private static InlineContent Build((int Kind, string Text, int Split)[] parts)
    {
        var elements = new List<InlineElement>();
        var open = new Stack<string>();
        var ids = 0;
        foreach (var (kind, text, split) in parts)
        {
            switch (kind)
            {
                case 0:
                    elements.Add(new InlineText(text));
                    break;
                case 1:
                    elements.Add(new InlineCode($"{++ids}", InlineCodeKind.Standalone));
                    break;
                case 2:
                    elements.Add(new InlineCode($"{++ids}", InlineCodeKind.Standalone) { Equiv = text });
                    break;
                case 3:
                    open.Push($"{++ids}");
                    elements.Add(new InlineCode(open.Peek(), InlineCodeKind.Opening));
                    break;
                case 5:
                    elements.Add(new InlineText(text[..split]));
                    elements.Add(new InlineCode($"{++ids}", InlineCodeKind.Standalone));
                    elements.Add(new InlineText(text[split..]));
                    break;
                default:
                    if (open.Count > 0)
                        elements.Add(new InlineCode(open.Pop(), InlineCodeKind.Closing));
                    break;
            }
        }

        while (open.Count > 0)
            elements.Add(new InlineCode(open.Pop(), InlineCodeKind.Closing));
        return new InlineContent(elements);
    }
}
