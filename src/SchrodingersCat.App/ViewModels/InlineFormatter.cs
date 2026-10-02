using System.Text;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>Plain-text rendering of segment content for display, with inline codes as ⟨id⟩ placeholders.</summary>
public static class InlineFormatter
{
    public static string Format(InlineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = new StringBuilder();
        foreach (var element in content)
        {
            text.Append(element switch
            {
                InlineText t => t.Text,
                InlineCode { Kind: InlineCodeKind.Standalone } c => $"⟨{c.Id}/⟩",
                InlineCode { Kind: InlineCodeKind.Opening } c => $"⟨{c.Id}⟩",
                InlineCode c => $"⟨/{c.Id}⟩",

                // Annotations (terms, comments, translate="no") don't change the text; they aren't shown yet.
                _ => "",
            });
        }

        return text.ToString();
    }
}
