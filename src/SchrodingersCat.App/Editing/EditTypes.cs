using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Editing;

/// <summary>A run of the target as the editor shows it: text, a code half (a chip), or an annotation half.</summary>
public abstract record EditToken;

public sealed record TextToken(string Text) : EditToken;

public sealed record CodeToken(InlineCode Code) : EditToken;

public sealed record MarkerToken(InlineMarker Marker) : EditToken;

public enum DeleteDirection
{
    /// <summary>Backspace: the position before the caret.</summary>
    Backward,

    /// <summary>Delete: the position after the caret.</summary>
    Forward,
}

/// <summary>
/// The outcome of an edit. A refused edit changes nothing and says why, so the editor can show it; an applied edit
/// may carry a note (e.g. protected codes that were kept).
/// </summary>
public sealed record EditResult(bool Applied, string? Message)
{
    public static EditResult Done { get; } = new(true, null);

    /// <summary>Nothing to do (e.g. Backspace at the start); not worth a message.</summary>
    public static EditResult Unchanged { get; } = new(false, null);

    public bool IsRefused => !Applied && Message is not null;

    public static EditResult Refused(string message) => new(false, message);
}
