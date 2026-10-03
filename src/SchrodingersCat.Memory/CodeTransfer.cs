using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>A match's target with the current source's codes, and whether every code found a place.</summary>
public sealed record TransferredTarget(InlineContent Target, bool AllCodesPlaced);

/// <summary>
/// Puts the current source's codes into a match's target (design section 3). A TM entry's codes belong to the
/// sentence it was made for; the target inserted into the current segment needs the current codes instead.
/// </summary>
/// <remarks>
/// <para>The entry's source codes pair with the current source's codes in order, per kind: the first standalone code
/// with the first standalone code, the first opening half with the first opening half. A closing half follows its
/// opening half; closing halves of pairs opened in another segment pair in order.</para>
/// <para>Each code in the match's target becomes its paired current code, with the current id and attributes, so the
/// code check is satisfied. Codes that don't pair up are left out, as are annotations (they describe the entry's
/// text, not this one); then <see cref="TransferredTarget.AllCodesPlaced"/> is false, and the translator inserts the
/// rest (F9).</para>
/// </remarks>
public static class CodeTransfer
{
    public static TransferredTarget Apply(TmEntry entry, InlineContent source)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);

        var map = Pair(entry.Source.OfType<InlineCode>().ToList(), source.OfType<InlineCode>().ToList());

        var placed = new HashSet<(string, InlineCodeKind)>();
        var allPlaced = true;
        var elements = new List<InlineElement>();
        foreach (var element in entry.Target)
        {
            switch (element)
            {
                case InlineText text:
                    elements.Add(text);
                    break;
                case InlineCode code when map.TryGetValue((code.Id, code.Kind), out var current) && placed.Add((current.Id, current.Kind)):
                    elements.Add(current);
                    break;
                case InlineCode:
                    allPlaced = false;
                    break;
            }
        }

        if (source.OfType<InlineCode>().Any(c => !placed.Contains((c.Id, c.Kind))))
            allPlaced = false;
        return new TransferredTarget(new InlineContent(elements), allPlaced);
    }

    // The entry's code halves → the current source's halves.
    private static Dictionary<(string, InlineCodeKind), InlineCode> Pair(List<InlineCode> entry, List<InlineCode> current)
    {
        var map = new Dictionary<(string, InlineCodeKind), InlineCode>();
        foreach (var kind in new[] { InlineCodeKind.Standalone, InlineCodeKind.Opening })
        {
            foreach (var (from, to) in entry.Where(c => c.Kind == kind).Zip(current.Where(c => c.Kind == kind)))
                map.TryAdd((from.Id, from.Kind), to);
        }

        // A closing half follows its opening half, if the current source has the matching closing half.
        var orphans = new List<InlineCode>();
        foreach (var closing in entry.Where(c => c.Kind == InlineCodeKind.Closing))
        {
            if (map.TryGetValue((closing.Id, InlineCodeKind.Opening), out var opening))
            {
                if (current.FirstOrDefault(c => c.Kind == InlineCodeKind.Closing && c.Id == opening.Id) is { } match)
                    map.TryAdd((closing.Id, closing.Kind), match);
            }
            else if (!entry.Any(c => c.Kind == InlineCodeKind.Opening && c.Id == closing.Id))
            {
                orphans.Add(closing);
            }
        }

        // Closing halves of pairs opened elsewhere pair in order.
        var currentOrphans = current.Where(c => c.Kind == InlineCodeKind.Closing && !current.Any(o => o.Kind == InlineCodeKind.Opening && o.Id == c.Id));
        foreach (var (from, to) in orphans.Zip(currentOrphans))
            map.TryAdd((from.Id, from.Kind), to);
        return map;
    }
}
