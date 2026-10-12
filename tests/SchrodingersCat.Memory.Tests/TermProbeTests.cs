using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// Step 6 of <c>docs/design/termbase.md</c>: trying one entry's terms on a text (<see cref="TermProbe"/>), following a
/// write in cached hits (<see cref="TermIndex.Merge"/>), and the entries that have a source term already
/// (<see cref="TermIndex.WithSourceTerm"/>).
/// </summary>
public class TermProbeTests
{
    private static readonly TermEntryId Id = new(7);

    [Theory]
    [InlineData("fájl", "Töröld a fájlt.")] // a suffix
    [InlineData("C#", "Learn C# today")]
    [InlineData("Wi-Fi", "Turn the wi‑fi off")] // a non-breaking hyphen
    [InlineData("profile", "このprofileを開く")] // a run of another script is a word of its own
    [InlineData("ファイル", "ファイルを開く")]
    public void AProbe_FindsWhatAnIndexOfItsEntryFinds(string term, string text)
    {
        var entry = Entry(term);
        var source = Text(text);

        var hit = TermProbe.Of(entry).Find(source, Id);

        Assert.NotNull(hit);
        Assert.Equal(Assert.Single(TermIndex.Of([new StoredTermEntry(Id, entry)]).Find(source)), hit);
        Assert.True(TermProbe.Of(entry).IsIn(source));
    }

    [Theory]
    [InlineData("files", "the file")]
    [InlineData("file", "このprofileを開く")]
    [InlineData("C#", "C#x")]
    [InlineData(" ", "a b")] // a blank term is never found
    public void AProbe_FindsNothingTheRuleDoesntFind(string term, string text)
    {
        Assert.Null(TermProbe.Of(Entry(term)).Find(Text(text), Id));
        Assert.False(TermProbe.Of(Entry(term)).IsIn(Text(text)));
    }

    // Across a code is a hit; only in a code's equiv text isn't (step 2's rule), for the probe as for the index.
    [Fact]
    public void AProbe_KeepsTheCodeRule()
    {
        var across = QaText.Of(new InlineContent([new InlineText("Save the file "), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("manager settings.")]));
        var inEquiv = QaText.Of(new InlineContent([new InlineText("Press "), new InlineCode("1", InlineCodeKind.Standalone) { Equiv = "OK" }, new InlineText(" now.")]));

        Assert.True(TermProbe.Of(Entry("file manager")).IsIn(across));
        Assert.False(TermProbe.Of(Entry("OK")).IsIn(inEquiv));
        Assert.Null(TermProbe.Of(Entry("OK")).Find(inEquiv, Id));
    }

    [Fact]
    public void AProbe_FindsEverySynonym_AndEveryOccurrence()
    {
        var entry = new TermEntry([new Term("folder"), new Term("directory")], [new Term("mappa")], null, default, null);

        var hit = TermProbe.Of(entry).Find(Text("A directory in a folder, and a folder."), Id)!;

        Assert.Equal([0, 1], hit.MatchedTerms);
        Assert.Equal([(2, 9), (17, 6), (31, 6)], hit.Occurrences.Select(o => (o.Start, o.Length)));
        Assert.Same(entry, TermProbe.Of(entry).Entry);
    }

    // ---------------------------------------------------------------- Merge

    [Fact]
    public void Merge_ReplacesTheChangedEntriesHits_InFindsOrder()
    {
        var source = Text("Save the file manager settings to a file.");
        var before = TermIndex.Of([Stored(1, "settings"), Stored(2, "file"), Stored(3, "save")]);
        var after = before.With(new TermEntryId(2), Entry("manager")).With(new TermEntryId(4), Entry("file manager"));

        var merged = TermIndex.Merge(before.Find(source), [new TermEntryId(2), new TermEntryId(4)],
            [TermProbe.Of(Entry("manager")).Find(source, new TermEntryId(2))!, TermProbe.Of(Entry("file manager")).Find(source, new TermEntryId(4))!]);

        Assert.Equal(after.Find(source), merged);
        Assert.Equal([3, 4, 2, 1], merged.Select(h => (int)h.Id.Value));
    }

    [Fact]
    public void Merge_ForADeletedEntry_LeavesItsHitOut()
    {
        var source = Text("Open the file.");
        var before = TermIndex.Of([Stored(1, "file"), Stored(2, "open")]);

        Assert.Equal(before.Without(new TermEntryId(1)).Find(source), TermIndex.Merge(before.Find(source), [new TermEntryId(1)], []));
    }

    // ---------------------------------------------------------------- WithSourceTerm

    [Fact]
    public void WithSourceTerm_FindsTheSameTerm_FoldedWithWhitespaceCollapsed()
    {
        var index = TermIndex.Of([
            Stored(1, "File Manager"),
            Stored(2, "file"),
            new StoredTermEntry(new TermEntryId(3), new TermEntry([new Term("directory"), new Term("FILE  manager")], [], null, default, null)),
            Stored(4, "files"),
            Stored(5, "Fájl manager"),
            Stored(6, "file manager settings"),
        ]);

        Assert.Equal([1, 3], index.WithSourceTerm(" file   manager ").Select(e => (int)e.Id.Value));
        Assert.Equal([2], index.WithSourceTerm("FILE").Select(e => (int)e.Id.Value)); // not "files", not "file manager"
        Assert.Equal([5], index.WithSourceTerm("fajl manager").Select(e => (int)e.Id.Value)); // accents
        Assert.Empty(index.WithSourceTerm("fil"));
        Assert.Empty(index.WithSourceTerm("  "));
    }

    [Fact]
    public void WithSourceTerm_ReturnsHomonyms_AndTermsWithPunctuationOrWithoutSpaces()
    {
        var index = TermIndex.Of([Stored(1, "file"), Stored(2, "File"), Stored(3, "C#"), Stored(4, "ファイル"), Stored(5, "%")]);

        Assert.Equal([1, 2], index.WithSourceTerm("file").Select(e => (int)e.Id.Value));
        Assert.Equal([3], index.WithSourceTerm("c#").Select(e => (int)e.Id.Value));
        Assert.Equal([4], index.WithSourceTerm("ファイル").Select(e => (int)e.Id.Value));
        Assert.Equal([5], index.WithSourceTerm("%").Select(e => (int)e.Id.Value));
    }

    // The rule takes a literal U+FFFC for a code, so such a term isn't found in its own text: it is looked for in every
    // entry instead (a regression the property test found).
    [Fact]
    public void WithSourceTerm_FindsATermWithALiteralObjectReplacementCharacter()
    {
        var index = TermIndex.Of([Stored(1, "1,5\uFFFC'%"), Stored(2, ">\t\uFFFC"), Stored(3, "\uFFFC")]);

        Assert.Equal([1], index.WithSourceTerm("1,5\uFFFC'%").Select(e => (int)e.Id.Value));
        Assert.Equal([2], index.WithSourceTerm("> \uFFFC").Select(e => (int)e.Id.Value));
        Assert.Equal([3], index.WithSourceTerm("\uFFFC").Select(e => (int)e.Id.Value));
    }

    [Theory]
    [InlineData("File", "file", true)]
    [InlineData("file  manager", " File manager", true)]
    [InlineData("fájl", "fajl", true)]
    [InlineData("file", "files", false)]
    [InlineData(" ", "", true)]
    public void SameTerm_FoldsAndCollapsesWhitespace(string a, string b, bool same) =>
        Assert.Equal(same, TermEntry.SameTerm(a, b));

    private static TermEntry Entry(string term) => new([new Term(term)], [new Term("x")], null, default, null);

    private static StoredTermEntry Stored(long id, string term) => new(new TermEntryId(id), Entry(term));

    private static QaText Text(string text) => QaText.Of(InlineContent.FromText(text));
}
