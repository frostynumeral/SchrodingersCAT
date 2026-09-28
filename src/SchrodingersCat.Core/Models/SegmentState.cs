namespace SchrodingersCat.Core.Models;

/// <summary>
/// Workflow state of a segment. Mirrors the four states defined by XLIFF 2.0,
/// so a round-trip through a file never loses information.
/// Finer-grained, tool-specific states go into <see cref="Segment.SubState"/>.
/// </summary>
public enum SegmentState
{
    /// <summary>Not translated yet (or target not yet confirmed).</summary>
    Initial,

    /// <summary>Translated by the translator, not yet reviewed.</summary>
    Translated,

    /// <summary>Checked by a reviewer.</summary>
    Reviewed,

    /// <summary>Signed off; should not be changed anymore.</summary>
    Final,
}
