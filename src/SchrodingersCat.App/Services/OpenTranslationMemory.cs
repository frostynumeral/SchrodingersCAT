using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Services;

/// <summary>
/// The translation memory the app has open (decision 1 of <c>docs/design/translation-memory.md</c>: one at a time),
/// which it owns: disposing waits for the queued work, then closes the TM.
/// </summary>
/// <remarks>
/// Every operation runs on a background thread, so the UI never waits on the TM, and in a queue, one at a time and in
/// the order asked: a SQLite connection can't be used from two threads at once, and the order matters. Ctrl+Enter
/// queues the store before moving on queues the next segment's lookup, so a repetition right below finds the
/// translation just stored.
/// </remarks>
/// <param name="name">How the matches pane names the TM, e.g. its file name.</param>
public sealed class OpenTranslationMemory(ITranslationMemory memory, string name, string sourceLanguage, string targetLanguage) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    public ITranslationMemory Memory { get; } = memory ?? throw new ArgumentNullException(nameof(memory));

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public string SourceLanguage { get; } = sourceLanguage ?? throw new ArgumentNullException(nameof(sourceLanguage));

    public string TargetLanguage { get; } = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));

    /// <summary>The TM file, for remembering it between sessions; null for a TM that isn't a file.</summary>
    public string? Path { get; private init; }

    /// <summary>Opens a TM file (<see cref="SqliteTranslationMemory.Open"/>), named after the file.</summary>
    /// <exception cref="TmFormatException">The file isn't a TM this version can use.</exception>
    public static OpenTranslationMemory Open(string path) => Wrap(SqliteTranslationMemory.Open(path), path);

    /// <summary>Creates a TM file (<see cref="SqliteTranslationMemory.Create"/>); fails if the file exists.</summary>
    public static OpenTranslationMemory Create(string path, string sourceLanguage, string targetLanguage) =>
        Wrap(SqliteTranslationMemory.Create(path, sourceLanguage, targetLanguage), path);

    /// <summary>
    /// The TM's matches for a source, with <see cref="TmMatch.Origin"/> set to <see cref="Name"/> and
    /// <paramref name="penalty"/> taken off every score (decision 3). Cancelling skips a lookup that hasn't started;
    /// one that has runs to the end.
    /// </summary>
    public Task<IReadOnlyList<TmMatch>> LookupAsync(
        InlineContent source, string? previousSource, string? nextSource, int penalty = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Enqueue<IReadOnlyList<TmMatch>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [.. Memory.Lookup(source, previousSource, nextSource)
                .Select(m => m with { Score = Math.Max(0, m.Score - penalty), Origin = m.Origin ?? Name })];
        });
    }

    /// <summary>
    /// Stores a confirmed translation. <paramref name="replacing"/> is the target the segment stored before: that pair
    /// is replaced (<see cref="ITranslationMemory.Replace"/>), so correcting a translation updates it instead of keeping
    /// both.
    /// </summary>
    public Task StoreAsync(TmEntry entry, InlineContent? replacing)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Enqueue(() =>
        {
            if (replacing is null)
                Memory.Add(entry);
            else
                Memory.Replace(entry with { Target = replacing }, entry);
            return true;
        });
    }

    /// <summary>Stores imported entries, in one transaction for a SQLite TM.</summary>
    public Task ImportAsync(IReadOnlyList<TmEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return Enqueue(() =>
        {
            Memory.AddRange(entries);
            return true;
        });
    }

    /// <summary>Every entry, oldest first, read in the queue (for export).</summary>
    public Task<IReadOnlyList<TmEntry>> EntriesAsync() => Enqueue<IReadOnlyList<TmEntry>>(() => [.. Memory.Entries()]);

    /// <summary>Waits for the queued work (whatever its outcome), then closes the TM. Nothing can be queued after.</summary>
    public async ValueTask DisposeAsync()
    {
        Task tail;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            tail = _tail;
        }

        await tail.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        (Memory as IDisposable)?.Dispose();
    }

    // Each operation continues the previous one, whether it succeeded, failed or was skipped, so a failure can't stop
    // the queue. The token is never passed to ContinueWith: a cancelled continuation would complete at once and let
    // the next operation start while the one before is still running.
    private Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _tail = task;
            return task;
        }
    }

    private static OpenTranslationMemory Wrap(SqliteTranslationMemory memory, string path) =>
        new(memory, System.IO.Path.GetFileName(path), memory.SourceLanguage, memory.TargetLanguage) { Path = path };
}
