using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>What changed in the open TM: some entries deleted, one updated, or (after a bulk change) anything.</summary>
public sealed record EntriesChange
{
    /// <summary>Anything may have changed: show everything again.</summary>
    public static EntriesChange Everything { get; } = new() { IsEverything = true };

    public bool IsEverything { get; private init; }

    public IReadOnlyCollection<TmEntryId> Deleted { get; init; } = [];

    public TmEntryId? Updated { get; init; }

    /// <summary>Who made the change, so it can skip showing its own change twice; null for the main window.</summary>
    public object? Source { get; init; }
}
