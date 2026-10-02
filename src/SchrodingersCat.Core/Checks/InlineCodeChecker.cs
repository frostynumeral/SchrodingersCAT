using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Checks;

/// <summary>
/// Checks a unit's target inline codes against its source codes, following the XLIFF 2.0 editing hints
/// (see <c>docs/design/inline-code-editing.md</c>).
/// </summary>
/// <remarks>
/// <para>Checks run per unit, because a pair may open in one segment and close in another. Only translated
/// segments that aren't ignorable are compared with the source: an untranslated segment isn't expected to contain
/// anything yet, and ignorables are left as extracted. Pairing is checked over the unit's whole target (ignorables
/// included), as the writer does.</para>
/// <para>Codes in a non-reorderable sequence (a <c>canReorder="firstNo"</c> code followed by <c>"no"</c> codes, at
/// least two codes) are treated as non-deletable and non-copyable even if the file doesn't say so: XLIFF requires
/// it, and the reader doesn't enforce it.</para>
/// </remarks>
public static class InlineCodeChecker
{
    public static IReadOnlyList<InlineCodeIssue> Check(TranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var issues = new List<InlineCodeIssue>();
        var source = SourceCodes(unit);
        var compared = unit.Segments.Where(s => !s.IsIgnorable && s.Target is not null).ToList();
        var expected = Halves(compared, s => s.Source);
        var actual = Halves(compared, s => s.Target!);

        CheckMissing(expected, actual, source, issues);
        CheckUnknownAndCopies(actual, source, issues);
        CheckPairing(unit, issues);
        CheckOrder(expected, actual, source, issues);
        CheckAnnotations(compared, issues);
        return issues;
    }

    // ---------------------------------------------------------------- the source side

    private sealed record Half(InlineCode Code, Segment Segment, int Position);

    private sealed class SourceCodeTable
    {
        public Dictionary<(string Id, InlineCodeKind Kind), InlineCode> ByIdAndKind { get; } = [];

        /// <summary>Ids of codes in a non-reorderable sequence, with the sequence they belong to (by its first code's id).</summary>
        public Dictionary<string, string> Sequence { get; } = new(StringComparer.Ordinal);

        public bool ContainsId(string id) =>
            ByIdAndKind.ContainsKey((id, InlineCodeKind.Standalone))
            || ByIdAndKind.ContainsKey((id, InlineCodeKind.Opening))
            || ByIdAndKind.ContainsKey((id, InlineCodeKind.Closing));

        public bool IsProtected(InlineCode code) => !code.CanDelete || Sequence.ContainsKey(code.Id);

        public bool IsCopyable(InlineCode code) => code.CanCopy && !Sequence.ContainsKey(code.Id);
    }

    private static SourceCodeTable SourceCodes(TranslationUnit unit)
    {
        var table = new SourceCodeTable();
        var codes = unit.Segments.SelectMany(s => s.Source).OfType<InlineCode>().ToList();
        foreach (var code in codes)
            table.ByIdAndKind.TryAdd((code.Id, code.Kind), code);

        // A sequence is a firstNo code and the "no" codes right after it. One code alone (both halves of a <pc>
        // included) is, per the spec, the same as canReorder="yes".
        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i].CanReorder != CanReorder.FirstNo)
                continue;

            var members = new List<string> { codes[i].Id };
            for (var j = i + 1; j < codes.Count && codes[j].CanReorder == CanReorder.No; j++)
                members.Add(codes[j].Id);

            var distinct = members.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count < 2)
                continue;
            foreach (var id in distinct)
                table.Sequence.TryAdd(id, codes[i].Id);
        }

        return table;
    }

    private static List<Half> Halves(List<Segment> segments, Func<Segment, InlineContent> content)
    {
        var halves = new List<Half>();
        foreach (var segment in segments)
        {
            foreach (var code in content(segment).OfType<InlineCode>())
                halves.Add(new Half(code, segment, halves.Count));
        }

        return halves;
    }

    // ---------------------------------------------------------------- checks

    // Reported once per code. A code with some of its halves present is a pairing problem, not a missing code.
    private static void CheckMissing(List<Half> expected, List<Half> actual, SourceCodeTable source, List<InlineCodeIssue> issues)
    {
        var present = actual.Select(h => (h.Code.Id, h.Code.Kind)).ToHashSet();
        foreach (var code in expected.GroupBy(h => h.Code.Id, StringComparer.Ordinal))
        {
            if (code.Any(h => present.Contains((h.Code.Id, h.Code.Kind))))
                continue;

            var first = code.First();
            var label = Describe(first.Code);
            issues.Add(source.IsProtected(first.Code)
                ? new InlineCodeIssue(InlineCodeIssueKind.MissingProtectedCode, IssueSeverity.Error, first.Segment, code.Key,
                    $"{label} is missing from the target, and it may not be deleted.")
                : new InlineCodeIssue(InlineCodeIssueKind.MissingCode, IssueSeverity.Warning, first.Segment, code.Key,
                    $"{label} is missing from the target."));
        }
    }

    private static void CheckUnknownAndCopies(List<Half> actual, SourceCodeTable source, List<InlineCodeIssue> issues)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var half in actual)
        {
            var code = half.Code;
            if (source.ByIdAndKind.ContainsKey((code.Id, code.Kind)) || !reported.Add(code.Id))
                continue;

            if (source.ContainsId(code.Id))
            {
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.UnknownCode, IssueSeverity.Error, half.Segment, code.Id,
                    $"{Describe(code)} doesn't match the source, where code '{code.Id}' is a different kind of code."));
                continue;
            }

            // A copy keeps the base code's original data (same dataRef), or names it with copyOf when it has none.
            var baseCode = code.CopyOf is { } copyOf
                ? source.ByIdAndKind.GetValueOrDefault((copyOf, code.Kind))
                : code.DataRef is { } dataRef
                    ? source.ByIdAndKind.Values.FirstOrDefault(c => c.Kind == code.Kind && c.DataRef == dataRef)
                    : null;

            if (baseCode is null)
            {
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.UnknownCode, IssueSeverity.Error, half.Segment, code.Id,
                    $"{Describe(code)} is not in the source and is not a copy of a source code."));
            }
            else if (!source.IsCopyable(baseCode))
            {
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.CopyNotAllowed, IssueSeverity.Error, half.Segment, code.Id,
                    $"{Describe(code)} is a copy of code '{baseCode.Id}', which may not be copied."));
            }
        }
    }

    // Over the whole target, ignorables included, in document order: what the writer requires.
    private static void CheckPairing(TranslationUnit unit, List<InlineCodeIssue> issues)
    {
        var open = new List<(InlineCode Code, Segment Segment)>();
        foreach (var segment in unit.Segments.Where(s => s.Target is not null))
        {
            foreach (var code in segment.Target!.OfType<InlineCode>().Where(c => !c.IsIsolated))
            {
                if (code.Kind == InlineCodeKind.Opening)
                {
                    open.Add((code, segment));
                }
                else if (code.Kind == InlineCodeKind.Closing)
                {
                    var index = open.FindIndex(o => o.Code.Id == code.Id);
                    if (index >= 0)
                        open.RemoveAt(index);
                    else
                        issues.Add(new InlineCodeIssue(InlineCodeIssueKind.Unpaired, IssueSeverity.Error, segment, code.Id,
                            $"The closing half of code '{code.Id}' has no opening half before it in the target."));
                }
            }
        }

        foreach (var (code, segment) in open)
        {
            issues.Add(new InlineCodeIssue(InlineCodeIssueKind.Unpaired, IssueSeverity.Error, segment, code.Id,
                $"The opening half of code '{code.Id}' has no closing half after it in the target."));
        }
    }

    private static void CheckOrder(List<Half> expected, List<Half> actual, SourceCodeTable source, List<InlineCodeIssue> issues)
    {
        // Only codes that are in both, compared by their position in the source.
        var sourcePosition = new Dictionary<(string, InlineCodeKind), Half>();
        foreach (var half in expected)
            sourcePosition.TryAdd((half.Code.Id, half.Code.Kind), half);
        var matched = actual
            .Where(h => sourcePosition.ContainsKey((h.Code.Id, h.Code.Kind)))
            .Select(h => (Target: h, Source: sourcePosition[(h.Code.Id, h.Code.Kind)]))
            .ToList();

        // Non-reorderable sequences: their codes must keep their relative order.
        foreach (var sequence in matched.Where(m => source.Sequence.ContainsKey(m.Target.Code.Id))
                     .GroupBy(m => source.Sequence[m.Target.Code.Id], StringComparer.Ordinal))
        {
            var positions = sequence.Select(m => m.Source.Position).ToList();
            if (!IsAscending(positions))
            {
                var first = sequence.First().Target;
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.SequenceBroken, IssueSeverity.Error, first.Segment, sequence.Key,
                    $"The codes of the fixed-order sequence starting with code '{sequence.Key}' are in a different order than in the source."));
            }
        }

        // Everything else may move; say so once per segment, as information.
        foreach (var segment in matched.GroupBy(m => m.Target.Segment))
        {
            var moved = segment.FirstOrDefault(m => m.Source.Segment != segment.Key);
            if (moved.Target is not null)
            {
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.Reordered, IssueSeverity.Info, segment.Key, moved.Target.Code.Id,
                    $"Code '{moved.Target.Code.Id}' is in a different segment than in the source."));
            }
            else if (!IsAscending(segment.Select(m => m.Source.Position).ToList()))
            {
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.Reordered, IssueSeverity.Info, segment.Key, segment.First().Target.Code.Id,
                    "The codes are in a different order than in the source."));
            }
        }
    }

    private static void CheckAnnotations(List<Segment> compared, List<InlineCodeIssue> issues)
    {
        var inTarget = compared.SelectMany(s => s.Target!).OfType<InlineMarker>()
            .Where(m => m.Kind == InlineMarkerKind.Opening)
            .Select(m => m.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var segment in compared)
        {
            foreach (var marker in segment.Source.OfType<InlineMarker>().Where(m => m.Kind == InlineMarkerKind.Opening && !inTarget.Contains(m.Id)))
            {
                issues.Add(new InlineCodeIssue(InlineCodeIssueKind.MissingAnnotation, IssueSeverity.Info, segment, marker.Id,
                    $"Annotation '{marker.Id}'{(marker.Type is null ? "" : $" ({marker.Type})")} is not in the target."));
            }
        }
    }

    private static bool IsAscending(List<int> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] < values[i - 1])
                return false;
        }

        return true;
    }

    private static string Describe(InlineCode code) =>
        code.Disp is { } disp ? $"Code '{code.Id}' ({disp})" : $"Code '{code.Id}'";
}
