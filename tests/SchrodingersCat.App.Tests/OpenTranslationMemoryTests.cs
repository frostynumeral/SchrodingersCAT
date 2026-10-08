using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>The open TM's queue: one operation at a time, in the order asked, and closing after the queued work.</summary>
public sealed class OpenTranslationMemoryTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    // What Ctrl+Enter relies on: a store queued before a lookup is done before it, even while the queue is busy.
    [Fact]
    public async Task TheQueue_RunsInOrder_SoALookupAfterAStoreFindsIt()
    {
        using var memory = new RecordingMemory();
        await using var open = new OpenTranslationMemory(memory, "TM", "en", "hu");
        memory.BlockNextLookup();

        var busy = open.LookupAsync(Text("Busy."), null, null, cancellationToken: TestContext.Current.CancellationToken);
        var store = open.StoreAsync(new TmEntry(Text("Hello."), Text("Szia.")), replacing: null);
        var lookup = open.LookupAsync(Text("Hello."), null, null, cancellationToken: TestContext.Current.CancellationToken);
        memory.Release();
        await Task.WhenAll(busy, store, lookup).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(["lookup Busy.", "add Hello.", "lookup Hello."], memory.Log);
        Assert.Equal("Szia.", Assert.Single(await lookup).Entry.Target.ToPlainText());
    }

    // A cancelled lookup is skipped, but the operation after it still waits for the one before.
    [Fact]
    public async Task ACancelledLookup_IsSkipped_WithoutLettingTheNextOneStartEarly()
    {
        using var memory = new RecordingMemory();
        await using var open = new OpenTranslationMemory(memory, "TM", "en", "hu");
        memory.BlockNextLookup();
        using var cancel = new CancellationTokenSource();

        var busy = open.LookupAsync(Text("Busy."), null, null, cancellationToken: TestContext.Current.CancellationToken);
        var cancelled = open.LookupAsync(Text("Skipped."), null, null, cancellationToken: cancel.Token);
        cancel.Cancel();
        var next = open.StoreAsync(new TmEntry(Text("Next."), Text("Következő.")), replacing: null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(["lookup Busy."], memory.Log); // still busy: nothing else may have started

        memory.Release();
        await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(["lookup Busy.", "add Next."], memory.Log);
    }

    [Fact]
    public async Task AFailure_DoesntStopTheQueue()
    {
        using var memory = new RecordingMemory { FailNextAdd = true };
        await using var open = new OpenTranslationMemory(memory, "TM", "en", "hu");

        var failing = open.StoreAsync(new TmEntry(Text("A."), Text("B.")), replacing: null);
        var lookup = open.LookupAsync(Text("A."), null, null, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TmFormatException>(() => failing);
        Assert.Empty(await lookup.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_WaitsForTheQueuedWork_ThenClosesTheTm()
    {
        using var memory = new RecordingMemory();
        var open = new OpenTranslationMemory(memory, "TM", "en", "hu");
        memory.BlockNextLookup();
        _ = open.LookupAsync(Text("Busy."), null, null, cancellationToken: TestContext.Current.CancellationToken);
        var store = open.StoreAsync(new TmEntry(Text("Hello."), Text("Szia.")), replacing: null);

        var disposing = open.DisposeAsync().AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(memory.IsDisposed);
        memory.Release();
        await disposing.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(store.IsCompletedSuccessfully);
        Assert.Equal(["lookup Busy.", "add Hello.", "dispose"], memory.Log);
        // Thrown at once: nothing can be queued on a closed TM.
        Assert.Throws<ObjectDisposedException>(() => { _ = open.LookupAsync(Text("Late."), null, null, cancellationToken: TestContext.Current.CancellationToken); });
    }

    [Fact]
    public async Task Lookups_TakeThePenaltyOff_AndNameTheTm()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text("Hello."), Text("Hallo.")));
        await using var open = new OpenTranslationMemory(tm, "Project TM", "en", "de");

        var match = Assert.Single(await open.LookupAsync(Text("Hello."), null, null, penalty: 1, TestContext.Current.CancellationToken));

        Assert.Equal((99, "Project TM"), (match.Score, match.Origin));
    }

    [Fact]
    public async Task StoringWithAPreviousTarget_ReplacesThatPair()
    {
        var tm = new InMemoryTranslationMemory();
        await using var open = new OpenTranslationMemory(tm, "TM", "en", "hu");

        await open.StoreAsync(new TmEntry(Text("Save"), Text("Mentéss")) { Document = "a.xlf" }, replacing: null);
        await open.StoreAsync(new TmEntry(Text("Save"), Text("Mentés")) { Document = "a.xlf" }, replacing: Text("Mentéss"));

        Assert.Equal(["Mentés"], tm.Entries().Select(e => e.Target.ToPlainText()));
    }

    [Fact]
    public async Task CreateAndOpen_UseTheFile_NamedAfterIt()
    {
        var path = _files.PathOf("project.sctm");
        await using (var created = OpenTranslationMemory.Create(path, "en", "hu"))
        {
            Assert.Equal(("project.sctm", "en", "hu", path), (created.Name, created.SourceLanguage, created.TargetLanguage, created.Path));
            await created.StoreAsync(new TmEntry(Text("Hello."), Text("Szia.")), replacing: null);
        }

        await using var opened = OpenTranslationMemory.Open(path);
        Assert.Equal(1, opened.Memory.Count);
    }

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    /// <summary>An in-memory TM that logs what it's asked to do, and can hold a lookup until released.</summary>
    private sealed class RecordingMemory : ITranslationMemory, IDisposable
    {
        private readonly InMemoryTranslationMemory _inner = new();
        private readonly List<string> _log = [];
        private readonly ManualResetEventSlim _gate = new(initialState: true);

        public bool FailNextAdd { get; set; }

        public bool IsDisposed { get; private set; }

        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_log)
                    return [.. _log];
            }
        }

        public int Count => _inner.Count;

        public void BlockNextLookup() => _gate.Reset();

        public void Release() => _gate.Set();

        public void Add(TmEntry entry)
        {
            Record($"add {entry.Source.ToPlainText()}");
            if (FailNextAdd)
            {
                FailNextAdd = false;
                throw new TmFormatException("Disk full.");
            }

            _inner.Add(entry);
        }

        public void AddRange(IEnumerable<TmEntry> entries) => _inner.AddRange(entries);

        public bool Remove(TmEntry entry) => _inner.Remove(entry);

        public void Replace(TmEntry previous, TmEntry entry) => _inner.Replace(previous, entry);

        public IEnumerable<TmEntry> Entries() => _inner.Entries();

        public StoredEntry? Find(TmEntryId id) => _inner.Find(id);

        public UpdateResult Update(TmEntryId id, InlineContent target, DateTimeOffset changed, string? changedBy) => _inner.Update(id, target, changed, changedBy);

        public int Delete(IReadOnlyCollection<TmEntryId> ids) => _inner.Delete(ids);

        public BrowseResult Browse(EntryFilter filter, TmEntryId? after = null, int pageSize = ITranslationMemory.DefaultPageSize) => _inner.Browse(filter, after, pageSize);

        public int DeleteMatching(EntryFilter filter) => _inner.DeleteMatching(filter);

        public IReadOnlyList<string> Documents() => _inner.Documents();

        public IReadOnlyList<string> Users() => _inner.Users();

        public bool Compact() => _inner.Compact();

        public IReadOnlyList<TmMatch> Lookup(
            InlineContent source, string? previousSource = null, string? nextSource = null,
            int threshold = Ranking.DefaultThreshold, int maxMatches = Ranking.DefaultMaxMatches)
        {
            Record($"lookup {source.ToPlainText()}");
            _gate.Wait(Patience);
            return _inner.Lookup(source, previousSource, nextSource, threshold, maxMatches);
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            Record("dispose");
            _gate.Dispose();
        }

        private void Record(string what)
        {
            lock (_log)
                _log.Add(what);
        }
    }
}
