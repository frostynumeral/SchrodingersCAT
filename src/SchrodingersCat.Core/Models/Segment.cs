namespace SchrodingersCat.Core.Models;

/// <summary>
/// The smallest unit the translator works on — usually one sentence.
/// A <see cref="TranslationUnit"/> contains one or more segments.
/// </summary>
/// <remarks>
/// Deliberately a plain class with no UI dependencies: the Avalonia layer will wrap it
/// in an observable view model instead of the Core depending on CommunityToolkit.Mvvm.
/// </remarks>
public sealed class Segment
{
    /// <summary>Segment id, unique within its file. Optional in XLIFF 2.0.</summary>
    public string? Id { get; set; }

    public required string Source { get; set; }

    /// <summary><c>null</c> means there is no target yet; an empty string is an intentionally empty translation.</summary>
    public string? Target { get; set; }

    public SegmentState State { get; set; } = SegmentState.Initial;

    /// <summary>
    /// Tool-specific refinement of <see cref="State"/> in <c>prefix:value</c> form
    /// (e.g. <c>cat:draft</c>). Preserved as-is so other tools' data is not lost.
    /// </summary>
    public string? SubState { get; set; }

    /// <summary>
    /// <c>true</c> for text between segments that is not meant to be translated
    /// (XLIFF 2.0 <c>&lt;ignorable&gt;</c>, usually whitespace). The editor should hide these.
    /// </summary>
    public bool IsIgnorable { get; set; }

    public bool HasTarget => Target is not null;
}
