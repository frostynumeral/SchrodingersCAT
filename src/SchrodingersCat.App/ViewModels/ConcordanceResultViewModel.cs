using System.Windows.Input;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One entry found by a concordance search: its source and translation with the hits marked, and where it comes from.</summary>
public sealed class ConcordanceResultViewModel
{
    /// <param name="editCommand">Edits a <see cref="StoredEntry"/> (the window's).</param>
    /// <param name="deleteCommand">Deletes a <see cref="StoredEntry"/> (the window's).</param>
    public ConcordanceResultViewModel(ConcordanceHit hit, string origin, ICommand? editCommand = null, ICommand? deleteCommand = null)
    {
        EditCommand = editCommand;
        DeleteCommand = deleteCommand;
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));
        SourceRuns = ContentRuns.BuildHighlight(hit.Source);
        TargetRuns = ContentRuns.BuildHighlight(hit.Target);
        Details = MatchViewModel.DetailsOf(origin, hit.Entry);
    }

    public ConcordanceHit Hit { get; }

    public IReadOnlyList<ContentRun> SourceRuns { get; }

    public IReadOnlyList<ContentRun> TargetRuns { get; }

    public string Details { get; }

    public ICommand? EditCommand { get; }

    public ICommand? DeleteCommand { get; }

    /// <summary>The TM entry, for editing or deleting it; null from a TM without ids.</summary>
    public StoredEntry? Stored => Hit.EntryId is { } id ? new StoredEntry(id, Hit.Entry) : null;
}
