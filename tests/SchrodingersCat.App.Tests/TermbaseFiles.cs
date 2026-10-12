using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>Real termbase files for the tests, and the entries they hold.</summary>
internal static class TermbaseFiles
{
    /// <summary>Creates a termbase file with these entries, closed again.</summary>
    public static string Write(string path, string sourceLanguage, string targetLanguage, params TermEntry[] entries)
    {
        using var termbase = SqliteTermbase.Create(path, sourceLanguage, targetLanguage);
        termbase.AddRange(entries);
        return path;
    }

    /// <summary>An entry with one source term and these translations.</summary>
    public static TermEntry Entry(string source, params (string Text, TermStatus Status)[] translations) =>
        new([new Term(source)], [.. translations.Select(t => new Term(t.Text, t.Status))], null, DateTimeOffset.UtcNow, "test");

    /// <summary>"file": fájl (preferred), állomány, akta (forbidden), with notes; "file manager": fájlkezelő.</summary>
    public static TermEntry[] Client { get; } =
    [
        Entry("file", ("állomány", TermStatus.Admitted), ("fájl", TermStatus.Preferred), ("akta", TermStatus.Forbidden)) with
        {
            Note = "Computer files only.",
        },
        Entry("file manager", ("fájlkezelő", TermStatus.Unspecified)),
    ];

    /// <summary>A termbase that records whether it was closed, around an in-memory one.</summary>
    public sealed class Tracking(string sourceLanguage = "en", string targetLanguage = "hu", params TermEntry[] entries) : ITermbase
    {
        private readonly InMemoryTermbase _inner = Filled(sourceLanguage, targetLanguage, entries);

        public bool IsDisposed { get; private set; }

        public string SourceLanguage => _inner.SourceLanguage;

        public string TargetLanguage => _inner.TargetLanguage;

        public TermIndex Index => _inner.Index;

        public TermEntryId Add(TermEntry entry) => _inner.Add(entry);

        public IReadOnlyList<TermEntryId> AddRange(IEnumerable<TermEntry> entries) => _inner.AddRange(entries);

        public bool Update(TermEntryId id, TermEntry entry) => _inner.Update(id, entry);

        public int Delete(IReadOnlyCollection<TermEntryId> ids) => _inner.Delete(ids);

        public bool Compact() => false;

        public void Dispose()
        {
            IsDisposed = true;
            _inner.Dispose();
        }

        private static InMemoryTermbase Filled(string sourceLanguage, string targetLanguage, TermEntry[] entries)
        {
            var termbase = new InMemoryTermbase(sourceLanguage, targetLanguage);
            termbase.AddRange(entries);
            return termbase;
        }
    }
}
