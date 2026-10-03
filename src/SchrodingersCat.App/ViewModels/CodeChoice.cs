using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One source code in the code picker (<c>Ctrl+Shift+F9</c>, design section 2).</summary>
/// <param name="Text">The chip label, with both halves of a pair: <c>⟨b…b⟩</c>.</param>
/// <param name="Description">The code's tooltip text, and whether it's missing from the target.</param>
public sealed record CodeChoice(string CodeId, string Text, string Description, bool IsMissing)
{
    /// <summary>Type-ahead: the label, the id or the description contains the typed text.</summary>
    public bool Matches(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return filter.Length == 0
            || Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || CodeId.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The unit's source codes, one per code, in source order: this segment's first, then the unit's others.</summary>
    public static IReadOnlyList<CodeChoice> For(TargetEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var ids = model.Segment.Source.OfType<InlineCode>().Select(c => c.Id)
            .Concat(model.Hints.SourceCodes.Select(c => c.Id))
            .Distinct(StringComparer.Ordinal);

        return [.. ids.Select(id =>
        {
            var halves = model.Hints.SourceHalves(id);
            var chip = ContentRuns.Chip(halves[0], model.Unit, model.Hints);
            var text = halves.Count == 2 ? $"⟨{chip.Label}…{chip.Label}⟩" : chip.Text;
            var missing = model.IsMissing(id);
            return new CodeChoice(id, text, (missing ? "Missing from the target.\n" : "") + chip.ToolTip, missing);
        })];
    }
}
