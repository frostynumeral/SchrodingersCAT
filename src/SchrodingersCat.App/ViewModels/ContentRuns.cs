using System.Text;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One piece of segment content as the read-only views show it: text or a code chip.</summary>
public abstract record ContentRun
{
    /// <summary>In a match's source: how this piece compares with the current source (design section 6).</summary>
    public DiffKind Difference { get; init; }

    /// <summary>In a concordance result: part of a hit.</summary>
    public bool IsHit { get; init; }
}

/// <summary>Text, styled by the annotations (<c>&lt;mrk&gt;</c>) it lies inside.</summary>
public sealed record TextRun(string Text, AnnotationStyle Annotations) : ContentRun
{
    /// <summary>For text in a termbase term (<see cref="AnnotationStyle.TermbaseTerm"/>): the term's translations, for the tooltip.</summary>
    public string? TermToolTip { get; init; }
}

/// <summary>
/// A termbase term found in the source, to be underlined (section 6 of <c>docs/design/termbase.md</c>): a range of one
/// text element of the content (<see cref="QaText.SpansOf"/>), and what the tooltip says about the term.
/// </summary>
/// <param name="Element">The index of the content's element; marks on other than text elements are ignored.</param>
public sealed record TermMark(int Element, int Start, int Length, string ToolTip);

/// <summary>How annotated text is shown. Annotations aren't chips; they style the text they cover.</summary>
[Flags]
public enum AnnotationStyle
{
    None = 0,

    /// <summary>A term (<c>type="term"</c>).</summary>
    Term = 1,

    /// <summary>A comment (<c>type="comment"</c>).</summary>
    Comment = 2,

    /// <summary>Text that must not be translated (<c>translate="no"</c>).</summary>
    NoTranslate = 4,

    /// <summary>A term the open termbase has (section 6 of <c>docs/design/termbase.md</c>), not an annotation of the file.</summary>
    TermbaseTerm = 8,
}

/// <summary>One code half, shown as a chip.</summary>
/// <param name="Label">The short label: <c>disp</c>, a name from <c>subType</c>/<c>type</c>, or the id.</param>
/// <param name="ToolTip">The native code, the plain-text equivalent and the editing hints in words.</param>
/// <param name="IsProtected">The code can't be deleted (<c>canDelete="no"</c>, or part of a fixed-order sequence).</param>
public sealed record ChipRun(string CodeId, InlineCodeKind Kind, string Label, string ToolTip, bool IsProtected) : ContentRun
{
    /// <summary>The label with the pairing shown: <c>⟨b</c> opens, <c>b⟩</c> closes, a standalone code has no bracket.</summary>
    public string Text => Kind switch
    {
        InlineCodeKind.Opening => $"⟨{Label}",
        InlineCodeKind.Closing => $"{Label}⟩",
        _ => Label,
    };

    /// <summary>The code check reports a problem with this code (design section 3); the chip gets a red outline.</summary>
    public bool HasProblem { get; init; }

    /// <summary>The code itself, so a copy of the content keeps it.</summary>
    public InlineCode? Code { get; init; }
}

/// <summary>Turns segment content into <see cref="ContentRun"/>s for the read-only chip views (design section 1).</summary>
public static class ContentRuns
{
    /// <param name="openAtStart">Annotations opened earlier in the unit and still open where this content starts.</param>
    /// <param name="terms">
    /// The termbase's terms found in the content: the text they cover is <see cref="AnnotationStyle.TermbaseTerm"/>,
    /// with their tooltips. Ranges outside their element are cut to it; marks on codes are ignored.
    /// </param>
    public static IReadOnlyList<ContentRun> Build(
        InlineContent content, TranslationUnit unit, InlineCodeHints hints, IEnumerable<InlineMarker> openAtStart, IReadOnlyList<TermMark>? terms = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(openAtStart);

        var open = openAtStart.ToList();
        var runs = new List<ContentRun>();
        for (var i = 0; i < content.Count; i++)
        {
            switch (content[i])
            {
                case InlineText { Text.Length: > 0 } text:
                    var style = StyleOf(open);
                    foreach (var (piece, tip) in TermPieces(text.Text, i, terms))
                        AddText(runs, piece, tip is null ? style : style | AnnotationStyle.TermbaseTerm, tip);
                    break;
                case InlineCode code:
                    runs.Add(Chip(code, unit, hints));
                    break;
                case InlineMarker marker:
                    Track(open, marker);
                    break;
            }
        }

        return runs;
    }

    // Text joins the run before it if it looks the same, so a term's marks split runs only where they start or end.
    private static void AddText(List<ContentRun> runs, string text, AnnotationStyle style, string? termToolTip)
    {
        if (runs.Count > 0 && runs[^1] is TextRun previous && previous.Annotations == style && previous.TermToolTip == termToolTip)
            runs[^1] = previous with { Text = previous.Text + text };
        else
            runs.Add(new TextRun(text, style) { TermToolTip = termToolTip });
    }

    // A text element cut at the boundaries of the marks on it: each piece with the tooltips of the marks covering it
    // (overlapping terms, such as "file manager" and "file", give both), or null if none does.
    private static IEnumerable<(string Text, string? ToolTip)> TermPieces(string text, int element, IReadOnlyList<TermMark>? terms)
    {
        var marks = (terms ?? [])
            .Where(m => m is not null && m.Element == element)
            .Select(m => (Start: Math.Clamp(m.Start, 0, text.Length), End: (int)Math.Clamp((long)m.Start + m.Length, 0, text.Length), m.ToolTip))
            .Where(m => m.End > m.Start)
            .ToList();
        if (marks.Count == 0)
        {
            yield return (text, null);
            yield break;
        }

        var cuts = marks.SelectMany(m => new[] { m.Start, m.End }).Append(0).Append(text.Length).Distinct().Order().ToList();
        for (var c = 0; c + 1 < cuts.Count; c++)
        {
            var (from, to) = (cuts[c], cuts[c + 1]);
            var tips = marks.Where(m => m.Start <= from && m.End >= to).Select(m => m.ToolTip).Distinct().ToList();
            yield return (text[from..to], tips.Count == 0 ? null : string.Join("\n\n", tips));
        }
    }

    /// <summary>
    /// The text run at a position of the runs, counted as <see cref="Slice"/> counts them (a character of text is one
    /// position, a chip too); null at a chip or past the end.
    /// </summary>
    public static TextRun? RunAt(IReadOnlyList<ContentRun> runs, int position)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var start = 0;
        foreach (var run in runs)
        {
            var length = run is TextRun text ? text.Text.Length : 1;
            if (position >= start && position < start + length)
                return run as TextRun;
            start += length;
        }

        return null;
    }

    /// <summary>
    /// A match's source compared with the current source (<see cref="SourceDiff"/>), for the matches pane. The match's
    /// codes belong to another sentence, so their chips say only what kind of code they are; the current source's
    /// codes (added) are chips as in the source column.
    /// </summary>
    public static IReadOnlyList<ContentRun> BuildDiff(IReadOnlyList<DiffPart> parts, TranslationUnit unit, InlineCodeHints hints)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(hints);

        return [.. parts
            .Where(p => p.Element is not InlineText { Text.Length: 0 })
            .Select(p => (p.Element switch
            {
                InlineText text => (ContentRun)new TextRun(text.Text, AnnotationStyle.None),
                InlineCode code when p.Kind == DiffKind.Added => Chip(code, unit, hints),
                InlineCode code => MatchChip(code),
                var other => throw new ArgumentException($"A diff has no {other.GetType().Name}.", nameof(parts)),
            }) with { Difference = p.Kind })];
    }

    /// <summary>
    /// A concordance result's side (<see cref="Concordance"/>), with the hits marked. Its codes belong to another
    /// sentence, so their chips say only what kind of code they are, like a match's own codes.
    /// </summary>
    public static IReadOnlyList<ContentRun> BuildHighlight(IReadOnlyList<HighlightPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var runs = new List<ContentRun>();
        foreach (var part in parts)
        {
            switch (part.Element)
            {
                case InlineText { Text.Length: > 0 } text:
                    runs.Add(new TextRun(text.Text, AnnotationStyle.None) { IsHit = part.IsHit });
                    break;
                case InlineCode code:
                    runs.Add(MatchChip(code));
                    break;
            }
        }

        return runs;
    }

    /// <summary>Marks the chips of the codes in <paramref name="problemCodes"/> as having a problem.</summary>
    public static IReadOnlyList<ContentRun> MarkProblems(IReadOnlyList<ContentRun> runs, IReadOnlySet<string> problemCodes)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(problemCodes);

        return problemCodes.Count == 0
            ? runs
            : [.. runs.Select(r => r is ChipRun chip && problemCodes.Contains(chip.CodeId) ? chip with { HasProblem = true } : r)];
    }

    /// <summary>
    /// The content between two positions of the runs, as a <c>SelectableTextBlock</c> counts them: a character of
    /// text is one position, a chip too. Annotations aren't runs, so they're left out.
    /// </summary>
    public static InlineContent Slice(IReadOnlyList<ContentRun> runs, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var elements = new List<InlineElement>();
        var position = 0;
        foreach (var run in runs)
        {
            switch (run)
            {
                case TextRun text:
                    var from = Math.Clamp(start - position, 0, text.Text.Length);
                    var to = Math.Clamp(end - position, 0, text.Text.Length);
                    if (to > from)
                        elements.Add(new InlineText(text.Text[from..to]));
                    position += text.Text.Length;
                    break;
                case ChipRun chip:
                    if (position >= start && position < end && chip.Code is { } code)
                        elements.Add(code);
                    position++;
                    break;
            }
        }

        return new InlineContent(elements);
    }

    /// <summary>One code half as a chip.</summary>
    public static ChipRun Chip(InlineCode code, TranslationUnit unit, InlineCodeHints hints)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(hints);

        return new ChipRun(code.Id, code.Kind, Label(code), ToolTip(code, unit, hints), hints.IsProtected(code.Id)) { Code = code };
    }

    /// <summary>The annotations still open after <paramref name="elements"/>, e.g. the preceding segments of a unit.</summary>
    public static IReadOnlyList<InlineMarker> OpenMarkersAfter(IEnumerable<InlineElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var open = new List<InlineMarker>();
        foreach (var marker in elements.OfType<InlineMarker>())
            Track(open, marker);
        return open;
    }

    /// <summary>The chip label (design section 1 and decision 7): <c>disp</c>, else a name from the type, else the id.</summary>
    public static string Label(InlineCode code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!string.IsNullOrEmpty(code.Disp))
            return code.Disp;

        return code.SubType switch
        {
            "xlf:b" => "b",
            "xlf:i" => "i",
            "xlf:u" => "u",
            "xlf:lb" => "↵",
            "xlf:pb" => "pb",
            _ => code.Type switch
            {
                "link" => "link",
                "image" => "img",
                _ => code.Id,
            },
        };
    }

    // A code of a TM entry: its id and hints mean nothing in this unit.
    private static ChipRun MatchChip(InlineCode code) => new(code.Id, code.Kind, Label(code), code.Kind switch
    {
        InlineCodeKind.Opening => "Start of a code in the match",
        InlineCodeKind.Closing => "End of a code in the match",
        _ => "A code in the match",
    }, IsProtected: false);

    private static string ToolTip(InlineCode code, TranslationUnit unit, InlineCodeHints hints)
    {
        var tip = new StringBuilder(code.Kind switch
        {
            InlineCodeKind.Opening => $"Start of code {code.Id}",
            InlineCodeKind.Closing => $"End of code {code.Id}",
            _ => $"Code {code.Id}",
        });

        if (code.DataRef is not null && unit.OriginalData.TryGetValue(code.DataRef, out var native))
            tip.Append('\n').Append(native);
        if (!string.IsNullOrEmpty(code.Equiv))
            tip.Append("\nReads as: ").Append(code.Equiv);
        if (code.CopyOf is not null)
            tip.Append("\nCopy of code ").Append(code.CopyOf);
        if (code.IsIsolated)
            tip.Append("\nThe other half is in another unit.");

        // Hints describe source codes; a copy in the target has its own id and is always deletable.
        if (hints.IsInSource(code.Id))
        {
            if (hints.IsProtected(code.Id))
                tip.Append("\nCan't be deleted.");
            if (!hints.IsCopyable(code.Id))
                tip.Append("\nCan't be copied.");
            if (hints.SequenceOf(code.Id) is not null)
                tip.Append("\nPart of a fixed-order sequence.");
        }

        return tip.ToString();
    }

    private static void Track(List<InlineMarker> open, InlineMarker marker)
    {
        if (marker.Kind == InlineMarkerKind.Opening)
            open.Add(marker);
        else
            open.RemoveAll(m => m.Id == marker.Id);
    }

    private static AnnotationStyle StyleOf(List<InlineMarker> open)
    {
        var style = AnnotationStyle.None;
        foreach (var marker in open)
        {
            if (marker.Translate == false)
                style |= AnnotationStyle.NoTranslate;
            style |= marker.Type switch
            {
                "term" => AnnotationStyle.Term,
                "comment" => AnnotationStyle.Comment,
                _ => AnnotationStyle.None,
            };
        }

        return style;
    }
}
