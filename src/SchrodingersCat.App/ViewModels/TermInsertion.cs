using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>How a term's translation goes into the target: the list's Insert button and the term picker (Ctrl+T).</summary>
internal static class TermInsertion
{
    /// <summary>
    /// Pastes the translation as text, as the concordance inserts (section 6): it replaces the selection, so a word
    /// selected as a placeholder can be replaced, and is one undoable edit. Returns the status line's message.
    /// </summary>
    public static string Apply(TargetEditModel model, string text)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);

        var result = model.Paste(InlineContent.FromText(text), from: null);
        return result.Message ?? $"Inserted the term “{text}”.";
    }
}
