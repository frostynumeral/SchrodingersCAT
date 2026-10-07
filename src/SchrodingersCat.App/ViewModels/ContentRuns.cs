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
}

/// <summary>Text, styled by the annotations (<c>&lt;mrk&gt;</c>) it lies inside.</summary>
public sealed record TextRun(string Text, AnnotationStyle Annotations) : ContentRun;

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
    public static IReadOnlyList<ContentRun> Build(InlineContent content, TranslationUnit unit, InlineCodeHints hints, IEnumerable<InlineMarker> openAtStart)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(openAtStart);

        var open = openAtStart.ToList();
        var runs = new List<ContentRun>();
        foreach (var element in content)
        {
            switch (element)
            {
                case InlineText { Text.Length: > 0 } text:
                    var style = StyleOf(open);
                    if (runs.Count > 0 && runs[^1] is TextRun previous && previous.Annotations == style)
                        runs[^1] = previous with { Text = previous.Text + text.Text };
                    else
                        runs.Add(new TextRun(text.Text, style));
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
