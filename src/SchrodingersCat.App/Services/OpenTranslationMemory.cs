using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Services;

/// <summary>
/// The translation memory the app has open (decision 1 of <c>docs/design/translation-memory.md</c>: one at a time).
/// Lookups run on a background thread, so moving between segments never waits on the TM, and one at a time, because
/// a SQLite connection can't be used from two threads at once.
/// </summary>
/// <param name="name">How the matches pane names the TM, e.g. its file name.</param>
public sealed class OpenTranslationMemory(ITranslationMemory memory, string name)
{
    private readonly Lock _gate = new();

    public ITranslationMemory Memory { get; } = memory ?? throw new ArgumentNullException(nameof(memory));

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>
    /// The TM's matches for a source, with <see cref="TmMatch.Origin"/> set to <see cref="Name"/>. Cancelling stops a
    /// lookup that hasn't started yet; one that has runs to the end, and its result is dropped.
    /// </summary>
    public Task<IReadOnlyList<TmMatch>> LookupAsync(
        InlineContent source, string? previousSource, string? nextSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Task.Run(() =>
        {
            IReadOnlyList<TmMatch> matches;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches = Memory.Lookup(source, previousSource, nextSource);
            }

            return (IReadOnlyList<TmMatch>)[.. matches.Select(m => m with { Origin = m.Origin ?? Name })];
        }, cancellationToken);
    }
}
