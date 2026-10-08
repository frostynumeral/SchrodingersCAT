using System.Text;
using SchrodingersCat.Core.Models;

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

    // Where each element's text starts in Text (an element with no text is left out).
    private readonly List<(int Start, int Element, int Length)> _pieces;

    private QaText(string text, List<(int, int, int)> pieces)
    {
        Text = text;
        _pieces = pieces;
    }

    public string Text { get; }

    public static QaText Of(InlineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = new StringBuilder();
        var pieces = new List<(int, int, int)>();
        for (var i = 0; i < content.Count; i++)
        {
            var piece = content[i] switch
            {
                InlineText t => t.Text,
                InlineCode { Kind: InlineCodeKind.Standalone, Equiv: { Length: > 0 } equiv } => equiv,
                InlineCode => CodeMark.ToString(),
                _ => "",
            };
            if (piece.Length == 0)
                continue;

            pieces.Add((text.Length, i, piece.Length));
            text.Append(piece);
        }

        return new QaText(text.ToString(), pieces);
    }

    /// <summary>Where a range of <see cref="Text"/> is in the content: its start's element, cut off at that element's end.</summary>
    public TextSpan? SpanOf(int start, int length)
    {
        foreach (var (pieceStart, element, pieceLength) in _pieces)
        {
            if (start < pieceStart || start >= pieceStart + pieceLength)
                continue;

            var offset = start - pieceStart;
            return new TextSpan(element, offset, Math.Min(length, pieceLength - offset));
        }

        return null;
    }
}

/// <summary>What a quality check sees of one segment: its source and target as text, and what mustn't be translated.</summary>
public sealed class QaSegment
{
    private QaSegment(TranslationUnit unit, Segment segment, IReadOnlyList<(int Start, int Length)> doNotTranslate)
    {
        Unit = unit;
        Segment = segment;
        Source = QaText.Of(segment.Source);
        Target = QaText.Of(segment.Target ?? InlineContent.Empty);
        DoNotTranslate = doNotTranslate;
    }

    public TranslationUnit Unit { get; }

    public Segment Segment { get; }

    public QaText Source { get; }

    public QaText Target { get; }

    /// <summary>The ranges of <see cref="Source"/>'s text inside a <c>translate="no"</c> annotation.</summary>
    public IReadOnlyList<(int Start, int Length)> DoNotTranslate { get; }

    /// <summary>The unit's glossary entries (<see cref="GlossaryReader"/>), read when first asked for.</summary>
    public IReadOnlyList<GlossaryEntry> Glossary => field ??= GlossaryReader.Read(Unit);

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
    /// segment's source.
    /// </summary>
    public static QaSegment Of(TranslationUnit unit, Segment segment)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(segment);

        // The translate="no" annotations open where this segment starts.
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var earlier in unit.Segments.TakeWhile(s => !ReferenceEquals(s, segment)))
            Track(earlier.Source, open);

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
        return new QaSegment(unit, segment, [.. ranges.Where(r => r.Item2 > 0)]);
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
