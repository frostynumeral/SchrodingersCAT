using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Checks;

/// <summary>
/// What a unit's source says about each of its inline codes: which exist, which may not be deleted or copied, and
/// which form non-reorderable sequences. Shared by <see cref="InlineCodeChecker"/> and the editor, so both apply
/// exactly the same rules.
/// </summary>
/// <remarks>
/// A code in a non-reorderable sequence (a <c>canReorder="firstNo"</c> code followed by <c>"no"</c> codes, at least
/// two codes) counts as non-deletable and non-copyable even if the file doesn't say so: XLIFF requires it, and the
/// reader doesn't enforce it (design decision 10 in <c>docs/design/inline-code-editing.md</c>). A lone
/// <c>firstNo</c> code is, per the spec, the same as <c>canReorder="yes"</c>.
/// </remarks>
public sealed class InlineCodeHints
{
    private readonly Dictionary<(string Id, InlineCodeKind Kind), InlineCode> _byIdAndKind = [];
    private readonly Dictionary<string, List<InlineCode>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sequence = new(StringComparer.Ordinal);

    public InlineCodeHints(TranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var codes = unit.Segments.SelectMany(s => s.Source).OfType<InlineCode>().ToList();
        SourceCodes = codes;
        foreach (var code in codes)
        {
            _byIdAndKind.TryAdd((code.Id, code.Kind), code);
            if (!_byId.TryGetValue(code.Id, out var halves))
                _byId[code.Id] = halves = [];
            halves.Add(code);
        }

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
                _sequence.TryAdd(id, codes[i].Id);
        }
    }

    /// <summary>Every code half in the unit's source, in document order (ignorables included).</summary>
    public IReadOnlyList<InlineCode> SourceCodes { get; }

    public InlineCode? SourceCode(string id, InlineCodeKind kind) => _byIdAndKind.GetValueOrDefault((id, kind));

    public bool IsInSource(string id) => _byId.ContainsKey(id);

    /// <summary>The halves of a source code, in document order (one for a standalone or isolated code, two for a pair).</summary>
    public IReadOnlyList<InlineCode> SourceHalves(string id) => _byId.TryGetValue(id, out var halves) ? halves : [];

    /// <summary>A source code that must not be left out of the target.</summary>
    public bool IsProtected(string id) => _byId.TryGetValue(id, out var halves) && (halves.Any(h => !h.CanDelete) || _sequence.ContainsKey(id));

    /// <summary>A source code the translator may duplicate. Codes not in the source aren't copyable.</summary>
    public bool IsCopyable(string id) => _byId.TryGetValue(id, out var halves) && halves.All(h => h.CanCopy) && !_sequence.ContainsKey(id);

    /// <summary>The id of the first code of the non-reorderable sequence this code belongs to, or null.</summary>
    public string? SequenceOf(string id) => _sequence.GetValueOrDefault(id);
}
