using Avalonia.Controls;
using Avalonia.Layout;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Controls;

/// <summary>
/// Builds chip controls, shared by the read-only views and the editor so both look the same. A code chip is a
/// <c>Border.chip</c> with the class <c>opening</c>, <c>closing</c> or <c>standalone</c>, plus <c>protected</c> for codes
/// that can't be deleted. An annotation half (editor only) is a small <c>Border.marker</c>. Their look comes from
/// styles, so states can be added without touching this code.
/// </summary>
internal static class Chips
{
    public static Border Create(ChipRun chip)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        label.Children.Add(new TextBlock { Classes = { "label" }, Text = chip.Text });
        if (chip.IsProtected)
            label.Children.Add(new TextBlock { Classes = { "lock" }, Text = "🔒" });

        var border = new Border
        {
            Classes = { "chip", KindClass(chip.Kind) },
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        if (chip.IsProtected)
            border.Classes.Add("protected");
        ToolTip.SetTip(border, chip.ToolTip);
        return border;
    }

    /// <summary>An annotation half: annotations can be moved and deleted in the target, like very small brackets.</summary>
    public static Border CreateMarker(InlineMarker marker)
    {
        var opening = marker.Kind == InlineMarkerKind.Opening;
        var what = (marker.Translate, marker.Type) switch
        {
            (false, _) => "don't translate",
            (_, "term") => "term",
            (_, "comment") => string.IsNullOrEmpty(marker.Value) ? "comment" : $"comment: {marker.Value}",
            (_, { } type) => type,
            _ => null,
        };

        var border = new Border
        {
            Classes = { "marker", opening ? "opening" : "closing" },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Classes = { "label" }, Text = opening ? "⟦" : "⟧" },
        };
        ToolTip.SetTip(border, (opening ? "Start of annotation " : "End of annotation ") + marker.Id + (what is null ? "" : $" ({what})"));
        return border;
    }

    private static string KindClass(InlineCodeKind kind) => kind switch
    {
        InlineCodeKind.Opening => "opening",
        InlineCodeKind.Closing => "closing",
        _ => "standalone",
    };
}
