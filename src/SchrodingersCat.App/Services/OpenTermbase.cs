using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Services;

/// <summary>
/// The termbase the app has open (decision 4 of <c>docs/design/termbase.md</c>: one at a time), which it owns:
/// disposing waits for the queued work, then closes the termbase.
/// </summary>
/// <remarks>
/// Lookups read <see cref="Index"/>, an immutable snapshot, from any thread without waiting. Writes (step 6) go
/// through a queue on a background thread, one at a time and in the order asked, as the TM's operations do
/// (<see cref="OpenTranslationMemory"/>); step 5 makes none, but disposing keeps the same contract.
/// </remarks>
/// <param name="name">How the app names the termbase: the file name without its extension, as the check's messages say "the termbase “client”".</param>
public sealed class OpenTermbase(ITermbase termbase, string name) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    public ITermbase Termbase { get; } = termbase ?? throw new ArgumentNullException(nameof(termbase));

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The termbase file, for remembering it between sessions; null for a termbase that isn't a file.</summary>
    public string? Path { get; private init; }

    public string SourceLanguage => Termbase.SourceLanguage;

    public string TargetLanguage => Termbase.TargetLanguage;

    /// <summary>The entries as they are now (<see cref="ITermbase.Index"/>).</summary>
    public TermIndex Index => Termbase.Index;

    /// <summary>
    /// Opens a termbase file (<see cref="SqliteTermbase.Open(string, CancellationToken)"/>) on a background thread:
    /// loading reads every entry into the index, which takes seconds for a large termbase (section 3). Once started,
    /// it always ends with the termbase or an exception, so the caller can dispose a result it no longer wants.
    /// </summary>
    /// <exception cref="TermbaseFormatException">The file isn't a termbase this version can use.</exception>
    /// <exception cref="FileNotFoundException">The file doesn't exist.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the file is closed again.</exception>
    public static Task<OpenTermbase> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        // The token goes to Open, which closes the file when cancelled, not to Task.Run, which would drop a termbase
        // opened just as the token was cancelled.
        return Task.Run(() => Wrap(SqliteTermbase.Open(path, cancellationToken), path), CancellationToken.None);
    }

    /// <summary>Creates a termbase file (<see cref="SqliteTermbase.Create"/>); fails if the file exists.</summary>
    public static OpenTermbase Create(string path, string sourceLanguage, string targetLanguage) =>
        Wrap(SqliteTermbase.Create(path, sourceLanguage, targetLanguage), path);

    /// <summary>Waits for the queued work (whatever its outcome), then closes the termbase. Nothing can be queued after.</summary>
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
        Termbase.Dispose();
    }

    // As OpenTranslationMemory's queue: each operation continues the previous one, whatever its outcome, and the token
    // is never passed to ContinueWith. Step 6 queues its writes here.
    internal Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _tail = task;
            return task;
        }
    }

    private static OpenTermbase Wrap(SqliteTermbase termbase, string path) =>
        new(termbase, System.IO.Path.GetFileNameWithoutExtension(path)) { Path = path };
}
