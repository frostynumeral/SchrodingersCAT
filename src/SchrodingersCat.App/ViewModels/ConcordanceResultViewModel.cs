using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One entry found by a concordance search: its source and translation with the hits marked, and where it comes from.</summary>
public sealed class ConcordanceResultViewModel
{
    public ConcordanceResultViewModel(ConcordanceHit hit, string origin)
    {
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));
        SourceRuns = ContentRuns.BuildHighlight(hit.Source);
        TargetRuns = ContentRuns.BuildHighlight(hit.Target);
        Details = MatchViewModel.DetailsOf(origin, hit.Entry);
    }

    public ConcordanceHit Hit { get; }

    public IReadOnlyList<ContentRun> SourceRuns { get; }

    public IReadOnlyList<ContentRun> TargetRuns { get; }

    public string Details { get; }
}
