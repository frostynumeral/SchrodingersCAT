using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Text;

namespace SchrodingersCat.Core.Checks;

/// <summary>
/// Content as the quality checks read it: plain text, with each code standing in as its <see cref="InlineCode.Equiv"/>
/// (standalone codes) or as one <see cref="CodeMark"/>, so a check never looks into native code, and words on either
/// side of a code don't run together. Annotations add nothing.
/// </summary>
public sealed class QaText
{
    /// <summary>What a code without an equivalent text stands as: U+FFFC, which is neither a letter, a digit nor whitespace.</summary>
    public const char CodeMark = '￼';

    // Where each element's text starts in Text (an element with no text is left out), and whether it's a code's: in
    // order and contiguous, so every index of Text is in exactly one piece.
    private readonly List<(int Start, int Element, int Length, bool IsCode)> _pieces;

    private QaText(string text, List<(int, int, int, bool)> pieces)
    {
        Text = text;
        _pieces = pieces;
    }

    public string Text { get; }

    /// <summary>The text tokenised and folded for the term rule, once however many terms are looked for in it.</summary>
    internal PreparedText Prepared => field ??= PreparedText.Of(Text);

    public static QaText Of(InlineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = new StringBuilder();
        var pieces = new List<(int, int, int, bool)>();
        for (var i = 0; i < content.Count; i++)
        {
            var (piece, isCode) = content[i] switch
            {
                InlineText t => (t.Text, false),
                InlineCode { Kind: InlineCodeKind.Standalone, Equiv: { Length: > 0 } equiv } => (equiv, true),
                InlineCode => (CodeMark.ToString(), true),
                _ => ("", false),
            };
            if (piece.Length == 0)
                continue;

            pieces.Add((text.Length, i, piece.Length, isCode));
            text.Append(piece);
        }

        return new QaText(text.ToString(), pieces);
    }

    /// <summary>Where a range of <see cref="Text"/> is in the content: its start's element, cut off at that element's end.</summary>
    public TextSpan? SpanOf(int start, int length)
    {
        foreach (var (pieceStart, element, pieceLength, _) in _pieces)
        {
            if (start < pieceStart || start >= pieceStart + pieceLength)
                continue;

            var offset = start - pieceStart;
            return new TextSpan(element, offset, Math.Min(length, pieceLength - offset));
        }

        return null;
    }

    /// <summary>
    /// Where a range of <see cref="Text"/> is in the content: every element it covers, in order, each cut to the range,
    /// codes included (an element with no text, such as an annotation's half, has nothing to cover). A range reaching
    /// outside the text is cut to it; an empty one covers nothing.
    /// </summary>
    public IReadOnlyList<TextSpan> SpansOf(int start, int length)
    {
        var from = Math.Max(start, 0);
        var to = (int)Math.Min((long)start + length, Text.Length);
        if (to <= from)
            return [];

        var spans = new List<TextSpan>();
        for (var i = PieceAt(from); i < _pieces.Count && _pieces[i].Start < to; i++)
        {
            var (pieceStart, element, pieceLength, _) = _pieces[i];
            var spanStart = Math.Max(from, pieceStart);
            spans.Add(new TextSpan(element, spanStart - pieceStart, Math.Min(to, pieceStart + pieceLength) - spanStart));
        }

        return spans;
    }

    /// <summary>Whether this character of <see cref="Text"/> is a code's (its equivalent text, or a code mark).</summary>
    internal bool IsInCode(int index) => index >= 0 && index < Text.Length && _pieces[PieceAt(index)].IsCode;

    /// <summary>
    /// Every place the term rule finds the term in <see cref="Text"/> (<see cref="Words.FindAll"/>), except those that
    /// start or end in a code: terms are text, not codes, though a code may be inside one (section 2 of
    /// <c>docs/design/termbase.md</c>).
    /// </summary>
    internal IEnumerable<(int Start, int Length)> FindAll(TermPattern term) =>
        Words.FindAll(Prepared, term).Where(r => !IsInCode(r.Start) && !IsInCode(r.Start + r.Length - 1));

    /// <summary>The first place <see cref="FindAll"/> finds the term; null if there's none.</summary>
    internal (int Start, int Length)? FindTerm(string term) => FindFirst(TermPattern.Of(term));

    /// <summary>The first place <see cref="FindAll"/> finds the term, classified already; null if there's none.</summary>
    internal (int Start, int Length)? FindFirst(TermPattern term)
    {
        foreach (var range in FindAll(term))
            return range;
        return null;
    }

    // The piece the index is in, or the first after it: the pieces are in order and contiguous.
    private int PieceAt(int index)
    {
        int low = 0, high = _pieces.Count - 1;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_pieces[middle].Start + _pieces[middle].Length <= index)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}

/// <summary>What a quality check sees of one segment: its source and target as text, and what mustn't be translated.</summary>
public sealed class QaSegment
{
    private readonly UnitContext _context;

    private QaSegment(UnitContext context, Segment segment, IReadOnlyList<(int Start, int Length)> doNotTranslate)
    {
        _context = context;
        Segment = segment;
        Source = QaText.Of(segment.Source);
        Target = QaText.Of(segment.Target ?? InlineContent.Empty);
        DoNotTranslate = doNotTranslate;
    }

    /// <summary>
    /// What every segment of a unit shares, read once per unit: its glossary, its source annotations, and where terms
    /// from outside the file come from.
    /// </summary>
    internal sealed class UnitContext(TranslationUnit unit, ITermSource? terms)
    {
        public TranslationUnit Unit { get; } = unit;

        public ITermSource? Terms { get; } = terms;

        public IReadOnlyList<GlossaryEntry> Glossary => field ??= GlossaryReader.Read(Unit);

        public IReadOnlyList<TermRecord> GlossaryTerms => field ??= [.. Glossary.Select(TermRecord.Of)];

        public IReadOnlySet<string> SourceMarkers => field ??=
            Unit.Segments.SelectMany(s => s.Source).OfType<InlineMarker>().Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
    }

    public TranslationUnit Unit => _context.Unit;

    public Segment Segment { get; }

    public QaText Source { get; }

    public QaText Target { get; }

    /// <summary>The ranges of <see cref="Source"/>'s text inside a <c>translate="no"</c> annotation.</summary>
    public IReadOnlyList<(int Start, int Length)> DoNotTranslate { get; }

    /// <summary>The unit's glossary entries (<see cref="GlossaryReader"/>), read once per unit.</summary>
    public IReadOnlyList<GlossaryEntry> Glossary => _context.Glossary;

    /// <summary>
    /// The term records to check this segment against: the unit's glossary, then what the term source finds for this
    /// segment's source, looked up the first time they're asked for.
    /// </summary>
    public IReadOnlyList<TermRecord> Terms => field ??=
        _context.Terms is { } terms ? [.. _context.GlossaryTerms, .. terms.Find(Source)] : _context.GlossaryTerms;

    /// <summary>Whether an annotation with this id is in the unit's source, in any segment.</summary>
    public bool HasSourceMarker(string markerId) => _context.SourceMarkers.Contains(markerId);

    /// <summary>
    /// What the annotation with this id covers in <see cref="Source"/>'s text, if it opens in this segment (to the
    /// segment's end if it closes in a later one); null if it doesn't open here.
    /// </summary>
    public (int Start, int Length)? MarkedRange(string markerId)
    {
        ArgumentNullException.ThrowIfNull(markerId);

        var position = 0;
        int? start = null;
        foreach (var element in Segment.Source)
        {
            if (element is InlineMarker marker && marker.Id == markerId)
            {
                if (marker.Kind == InlineMarkerKind.Opening)
                    start = position;
                else if (start is { } from)
                    return (from, position - from);
            }
            else
            {
                position += QaText.Of(new InlineContent([element])).Text.Length;
            }
        }

        return start is { } open ? (open, position - open) : null;
    }

    /// <summary>The texts of <see cref="DoNotTranslate"/>, trimmed, without code marks; empty ones left out.</summary>
    public IEnumerable<string> DoNotTranslateTexts => DoNotTranslate
        .Select(r => Source.Text.Substring(r.Start, r.Length).Replace(QaText.CodeMark.ToString(), "", StringComparison.Ordinal).Trim())
        .Where(t => t.Length > 0);

    /// <summary>
    /// A segment of a unit. Annotations can span segments, so <c>translate="no"</c> may have opened in an earlier
    /// segment's source. <paramref name="terms"/> adds term records from outside the file, such as a termbase.
    /// </summary>
    public static QaSegment Of(TranslationUnit unit, Segment segment, ITermSource? terms = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(segment);

        // The translate="no" annotations open where this segment starts.
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var earlier in unit.Segments.TakeWhile(s => !ReferenceEquals(s, segment)))
            Track(earlier.Source, open);
        return Of(new UnitContext(unit, terms), segment, open);
    }

    /// <summary>
    /// Every segment of a unit in order, sharing one <see cref="UnitContext"/> and following the <c>translate="no"</c>
    /// annotations along, so a unit's check reads it once rather than once per segment.
    /// </summary>
    internal static IEnumerable<QaSegment> OfUnit(TranslationUnit unit, ITermSource? terms = null)
    {
        var context = new UnitContext(unit, terms);
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in unit.Segments)
        {
            yield return Of(context, segment, [.. open]);
            Track(segment.Source, open);
        }
    }

    private static QaSegment Of(UnitContext context, Segment segment, HashSet<string> open)
    {

        var ranges = new List<(int, int)>();
        var position = 0;
        int? start = open.Count > 0 ? 0 : null;
        foreach (var element in segment.Source)
        {
            if (element is InlineMarker marker)
            {
                Track([marker], open);
                if (open.Count > 0 && start is null)
                    start = position;
                else if (open.Count == 0 && start is { } from)
                {
                    ranges.Add((from, position - from));
                    start = null;
                }
            }
            else
            {
                position += QaText.Of(new InlineContent([element])).Text.Length;
            }
        }

        if (start is { } last)
            ranges.Add((last, position - last));
        return new QaSegment(context, segment, [.. ranges.Where(r => r.Item2 > 0)]);
    }

    private static void Track(IEnumerable<InlineElement> content, HashSet<string> open)
    {
        foreach (var marker in content.OfType<InlineMarker>())
        {
            if (marker.Kind == InlineMarkerKind.Closing)
                open.Remove(marker.Id);
            else if (marker.Translate == false)
                open.Add(marker.Id);
        }
    }
}
