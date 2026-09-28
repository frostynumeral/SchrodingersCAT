namespace SchrodingersCat.Core.Models;

/// <summary>What part of the unit a <see cref="Note"/> refers to.</summary>
public enum NoteAppliesTo
{
    Source,
    Target,
}

/// <summary>A comment attached to a <see cref="TranslationUnit"/> (by the developer, translator or reviewer).</summary>
public sealed class Note
{
    public string? Id { get; set; }

    public required string Text { get; set; }

    /// <summary>Free-form category, e.g. <c>context</c>, <c>reviewer</c>.</summary>
    public string? Category { get; set; }

    /// <summary>1 (highest) to 10 (lowest); <c>null</c> means the XLIFF default of 1.</summary>
    public int? Priority { get; set; }

    /// <summary><c>null</c> means the note applies to the whole unit.</summary>
    public NoteAppliesTo? AppliesTo { get; set; }
}
