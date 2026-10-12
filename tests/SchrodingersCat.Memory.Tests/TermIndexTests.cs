using System.Runtime.CompilerServices;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory.Tests;

/// <summary>Step 2 of <c>docs/design/termbase.md</c>: term entries, and the index that finds them in a source.</summary>
public class TermIndexTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the word rule

    [Theory]
    [InlineData("fájl", "Töröld a fájlt.", 9, 5)] // a word beginning: Hungarian endings
    [InlineData("file manager", "Open the File  Managers", 9, 14)] // every word a beginning, whitespace any
    [InlineData("Fajl", "TÖRÖLD A FÁJLT", 9, 5)] // case and accents
    [InlineData("strasse", "Die Straße", 4, 6)] // ß → ss
    [InlineData("Straße", "DIE STRASSE", 4, 7)]
    [InlineData("Gros", "Ein großartiger Tag", 4, 11)]
    [InlineData("s", "ß", 0, 1)] // a word's folded beginning: only the substring rule needs whole characters
    [InlineData("tab", "the table", 4, 5)] // a known limitation
    public void AWordTerm_IsFoundByItsWordsBeginnings(string term, string text, int start, int length) =>
        Assert.Equal([(start, length)], Places(Find(text, term)));

    [Theory]
    [InlineData("Datei", "Die Textdatei")] // not inside a compound, a known limitation
    [InlineData("file manager", "the manager file")]
    [InlineData("files", "the file")]
    public void AWordTerm_IsntFoundElsewhere(string term, string text) => Assert.Empty(Find(text, term));

    // ---------------------------------------------------------------- every occurrence

    [Fact]
    public void EveryOccurrence_IsFound_InOrder()
    {
        var hit = Assert.Single(Find("file, File and files", "file"));

        Assert.Equal([(0, 4), (6, 4), (15, 5)], hit.Occurrences.Select(o => (o.Start, o.Length)));
    }

    [Fact]
    public void ASymbolTerm_IsFoundEverywhere() =>
        Assert.Equal([(2, 1), (12, 1)], Places(Find("50% off, 20 % more", "%")));

    // Overlapping occurrences of one term are all kept.
    [Fact]
    public void OverlappingOccurrences_AreAllFound() =>
        Assert.Equal([(0, 2), (1, 2)], Places(Find("あああ", "ああ")));

    // ---------------------------------------------------------------- scripts without spaces

    [Theory]
    [InlineData("ファイル", "ファイルを開く、ファイル", new[] { 0, 4, 8, 4 })]
    [InlineData("文件", "打开文件夹", new[] { 2, 2 })]
    [InlineData("profile", "このprofileを開く", new[] { 2, 7 })] // runs of other scripts are words of their own
    [InlineData("このprofile", "このprofileを開く", new[] { 0, 9 })]
    [InlineData("C#", "このC#を学ぶ", new[] { 2, 2 })]
    [InlineData("ファイル", "ファイルを", new[] { 0, 4 })]
    public void ATermInAScriptWithoutSpaces_IsFoundInside(string term, string text, int[] places) =>
        Assert.Equal(places.Chunk(2).Select(p => (p[0], p[1])), Places(Find(text, term)));

    [Fact]
    public void AThaiTerm_IsFoundInsideAClause()
    {
        const string text = "ภาษาไทยง่าย";

        Assert.Equal([(text.IndexOf("ไทย", StringComparison.Ordinal), 3)], Places(Find(text, "ไทย")));
    }

    [Theory]
    [InlineData("file", "このprofileを開く")]
    [InlineData("カ", "ガイド")] // カ (ka) isn't in ガ (ga)
    [InlineData("か", "がいど")] // nor in a decomposed が
    [InlineData("をs", "をß")] // nor half of ß's "ss"
    public void ASubstring_StartsAndEndsWhereCharactersDo(string term, string text) => Assert.Empty(Find(text, term));

    // ß is "ss" once, not twice.
    [Theory]
    [InlineData("をss", "をß", 0, 2)]
    [InlineData("ssを", "ßを", 0, 2)]
    public void ASubstringCoveringAWholeFold_IsFoundOnce(string term, string text, int start, int length) =>
        Assert.Equal([(start, length)], Places(Find(text, term)));

    // ---------------------------------------------------------------- terms with punctuation (step 1's rows)

    [Theory]
    [InlineData("C#", "C# and c#", new[] { 0, 2, 7, 2 })]
    [InlineData("C#", "Learn C#.", new[] { 6, 2 })]
    [InlineData("C#", "Learn C#x and C# today", new[] { 14, 2 })]
    [InlineData(".NET", "Install .NET 10", new[] { 8, 4 })]
    [InlineData(".NET", "the .NETs", new[] { 4, 4 })]
    [InlineData("Wi-Fi", "Turn on wi-fi", new[] { 8, 5 })]
    [InlineData("Wi-Fi", "Turn on Wi‑Fi", new[] { 8, 5 })]
    [InlineData("Wi-Fi", "Kapcsold be a Wi-Fi-t", new[] { 14, 5 })]
    [InlineData("e-mail", "Küldj e-mailt", new[] { 6, 6 })]
    [InlineData("don't", "Don’t panic", new[] { 0, 5 })]
    [InlineData("x-Gros", "Ein x-großartiger Tag", new[] { 4, 6 })] // ends inside ß's "ss"
    public void ATermWithPunctuation_IsFoundAsWritten(string term, string text, int[] places) =>
        Assert.Equal(places.Chunk(2).Select(p => (p[0], p[1])), Places(Find(text, term)));

    [Theory]
    [InlineData("C#", "Learn C")]
    [InlineData("C#", "Learn Cobol")]
    [InlineData("C#", "Learn C#x")]
    [InlineData("C#", "Learn \U0001D400C#")]
    [InlineData("C++", "Use C+ here")]
    [InlineData(".NET", "Install ASP.NET")]
    [InlineData("Wi-Fi", "Turn on Wi Fi")]
    [InlineData("Wi-Fi", "Turn on WiFi")]
    [InlineData("Wi-Fi", "Turn on HiWi-Fi")]
    public void ATermWithPunctuation_IsntFoundOtherwise(string term, string text) => Assert.Empty(Find(text, term));

    // ---------------------------------------------------------------- long first words (the key's cut)

    // A term is filed under at most Words.MaxKeyLength (16) characters of its first word run, and the text is probed
    // with as many: first words around the cut, and longer, are found, suffixed, and with a code inside.
    [Theory]
    [InlineData("abcdefghijklmno", "x abcdefghijklmno y", 2, 15)]
    [InlineData("abcdefghijklmnop", "x abcdefghijklmnop y", 2, 16)]
    [InlineData("abcdefghijklmnopq", "x abcdefghijklmnopq y", 2, 17)]
    [InlineData("Donaudampfschifffahrt", "Die Donaudampfschifffahrt", 4, 21)]
    [InlineData("Donaudampfschifffahrt", "Die Donaudampfschifffahrtsgesellschaft", 4, 34)]
    [InlineData("Donaudampfschifffahrt Wien", "Die DONAUDAMPFSCHIFFFAHRT  Wien", 4, 27)]
    [InlineData("Donaudampfschifffahrt-Wien", "Die Donaudampfschifffahrt-Wien", 4, 26)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwxyz", 0, 26)]
    public void ALongFirstWord_IsFound(string term, string text, int start, int length) =>
        Assert.Equal([(start, length)], Places(Find(text, term)));

    // The first 16 characters agree, the rest doesn't: the key matches, the rule doesn't.
    [Theory]
    [InlineData("Donaudampfschifffahrt", "Die Donaudampfschiffahrt")]
    [InlineData("abcdefghijklmnopq", "abcdefghijklmnopx")]
    public void ALongFirstWord_DifferingAfterTheKey_IsntFound(string term, string text) => Assert.Empty(Find(text, term));

    // A code inside the first word of a term with punctuation is skipped (a word term would be split there): the
    // key is the run around it.
    [Theory]
    [InlineData(5)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void ALongFirstWord_IsFoundWithACodeInside(int at) =>
        Assert.Equal([(0, 27)], Places(Index(["Donaudampfschifffahrt-Wien"]).Find(QaText.Of(Content(
            "Donaudampfschifffahrt-Wien"[..at], Ph(), "Donaudampfschifffahrt-Wien"[at..])))));

    // ---------------------------------------------------------------- several entries and terms

    // Every overlapping hit is kept: the longer first where they start together.
    [Fact]
    public void OverlappingTerms_AreAllFound_TheLongerFirst()
    {
        var index = Index(["file"], ["file manager"], ["manager"]);

        var hits = index.Find(Text("the file manager"));

        Assert.Equal([2, 1, 3], hits.Select(h => h.Id.Value));
        Assert.Equal([(4, 12), (4, 4), (9, 7)], hits.Select(h => (h.Occurrences[0].Start, h.Occurrences[0].Length)));
    }

    // Two concepts for "file": both found, in the order of their ids.
    [Fact]
    public void Homonyms_AreFoundApart()
    {
        var hits = Index(["file"], ["file"]).Find(Text("Save the file."));

        Assert.Equal([1, 2], hits.Select(h => h.Id.Value));
        Assert.Equal(hits[0].Occurrences, hits[1].Occurrences);
    }

    [Fact]
    public void Synonyms_AreOneHit_WithEveryTermFound()
    {
        var hit = Assert.Single(Index(["folder", "directory"]).Find(Text("Move the folder into the directory.")));

        Assert.Equal([0, 1], hit.MatchedTerms);
        Assert.Equal([new TermOccurrence(0, 9, 6), new TermOccurrence(1, 25, 9)], hit.Occurrences);
    }

    // Synonyms found at the same place: one occurrence, under the first, but both matched.
    [Fact]
    public void SynonymsAtTheSamePlace_AreOneOccurrence()
    {
        var hit = Assert.Single(Index(["File", "file"]).Find(Text("the file")));

        Assert.Equal([0, 1], hit.MatchedTerms);
        Assert.Equal([new TermOccurrence(0, 4, 4)], hit.Occurrences);
    }

    // A forbidden source synonym still names the concept.
    [Fact]
    public void AForbiddenSourceSynonym_IsFound()
    {
        var entry = new TermEntry([new Term("folder", TermStatus.Preferred), new Term("dir", TermStatus.Forbidden)], [], null, Monday, null);

        var hit = Assert.Single(TermIndex.Empty.With(new TermEntryId(1), entry).Find(Text("Open the dir.")));

        Assert.Equal([1], hit.MatchedTerms);
    }

    // ---------------------------------------------------------------- codes

    [Fact]
    public void ATerm_IsFoundAcrossAClosingCode() =>
        Assert.Equal([(1, 15)], Places(Index(["Wi-Fi settings"]).Find(QaText.Of(Content(
            new InlineCode("1", InlineCodeKind.Opening), "Wi-Fi", new InlineCode("1", InlineCodeKind.Closing), " settings")))));

    [Fact]
    public void AWordTerm_IsFoundAcrossAStandaloneCode() =>
        Assert.Equal([(0, 12)], Places(Index(["file manager"]).Find(QaText.Of(Content("file", Ph(), "manager")))));

    // A code inside the first word: the term is filed under its whole first word run, not its first token (w).
    [Fact]
    public void ATermWithPunctuation_IsFoundWithACodeInItsFirstWord() =>
        Assert.Equal([(8, 6)], Places(Index(["Wi-Fi"]).Find(QaText.Of(Content("Turn on W", Ph(), "i-Fi")))));

    [Fact]
    public void ATermWithPunctuation_IsFoundWithACodeAfterItsPunctuation() =>
        Assert.Equal([(8, 5)], Places(Index([".NET"]).Find(QaText.Of(Content("Install .", Ph(), "NET")))));

    // A code in the middle is allowed, even one standing as its equiv text.
    [Fact]
    public void ATerm_IsFoundWithAnEquivInside() =>
        Assert.Equal([(0, 12)], Places(Index(["Press OK now"]).Find(QaText.Of(Content("Press ", Ph("OK"), " now")))));

    [Fact]
    public void ATermOnlyInAnEquiv_IsntFound() =>
        Assert.Empty(Index(["Save"]).Find(QaText.Of(Content("Click ", Ph("Save"), " now"))));

    [Fact]
    public void ATermInAnEquivAndInTheText_IsFoundInTheText() =>
        Assert.Equal([(8, 4)], Places(Index(["Save"]).Find(QaText.Of(Content(Ph("Save"), " or Save")))));

    // A hit that starts or ends in a code doesn't count; a word running into an equiv is such a hit.
    [Theory]
    [InlineData("files", "s")]
    [InlineData("file", "name")]
    public void AWordRunningIntoAnEquiv_IsntFound(string term, string equiv) =>
        Assert.Empty(Index([term]).Find(QaText.Of(Content("Open the file", Ph(equiv)))));

    // A place the code rule refuses doesn't hide the next one.
    [Fact]
    public void ARefusedPlace_DoesntHideTheNext() =>
        Assert.Equal([(1, 2)], Places(Index(["ああ"]).Find(QaText.Of(Content(Ph("あ"), "ああ")))));

    [Fact]
    public void IsInCode_IsTrueOnCodesOnly()
    {
        var text = QaText.Of(Content("a", Ph("OK"), "￼", Ph(), "b"));

        Assert.Equal([false, true, true, false, true, false], Enumerable.Range(0, text.Text.Length).Select(text.IsInCode));
        Assert.False(text.IsInCode(-1));
        Assert.False(text.IsInCode(text.Text.Length));
    }

    // ---------------------------------------------------------------- no term

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\uD800")] // a lone surrogate
    [InlineData("￼")] // a code mark
    [InlineData("́")] // folds to nothing
    public void ATermThatCantBeThere_IsntFound(string term) =>
        Assert.Empty(Index([term]).Find(QaText.Of(Content("plain ", Ph(), " text́"))));

    [Fact]
    public void AnEntryWithOnlyBlankTerms_IsntFound() =>
        Assert.Empty(Index(["", " "]).Find(Text("anything")));

    [Fact]
    public void AnEmptyIndexOrText_FindsNothing()
    {
        Assert.Empty(TermIndex.Empty.Find(Text("the file")));
        Assert.Empty(Index(["file"]).Find(QaText.Of(InlineContent.Empty)));
    }

    // ---------------------------------------------------------------- snapshots

    [Fact]
    public void With_LeavesTheOldIndexAsItWas()
    {
        var before = Index(["file"]);

        var after = before.With(new TermEntryId(2), Entry("folder"));

        Assert.Equal((1, 2), (before.Count, after.Count));
        Assert.Empty(before.Find(Text("the folder")));
        Assert.Single(after.Find(Text("the folder")));
    }

    [Fact]
    public void With_AnExistingId_ReplacesTheEntry()
    {
        var index = Index(["file"]).With(new TermEntryId(1), Entry("folder"));

        Assert.Equal(1, index.Count);
        Assert.Empty(index.Find(Text("the file")));
        Assert.Equal(Entry("folder"), Assert.Single(index.Find(Text("the folder"))).Entry);
        Assert.Equal(Entry("folder"), index.Find(new TermEntryId(1)));
    }

    [Fact]
    public void Without_RemovesTheEntry_AndAStaleIdChangesNothing()
    {
        var index = Index(["file"], ["folder"]);

        var without = index.Without(new TermEntryId(1));

        Assert.Empty(without.Find(Text("the file")));
        Assert.Null(without.Find(new TermEntryId(1)));
        Assert.Same(without, without.Without(new TermEntryId(1)));
        Assert.Same(index, index.WithoutRange([new TermEntryId(7), new TermEntryId(8)]));
        Assert.Single(index.Find(Text("the file")));
    }

    [Fact]
    public void WithoutRange_IsRepeatedWithout()
    {
        var index = Index(["file"], ["folder"], ["manager"]);
        var text = Text("the file manager folder");

        var range = index.WithoutRange([new TermEntryId(1), new TermEntryId(3), new TermEntryId(9)]);
        var repeated = index.Without(new TermEntryId(1)).Without(new TermEntryId(3));

        Assert.Equal(repeated.Entries(), range.Entries());
        Assert.Equal(repeated.Find(text), range.Find(text));
        Assert.Equal([2], range.Find(text).Select(h => h.Id.Value));
    }

    [Fact]
    public void Of_IsRepeatedWith()
    {
        StoredTermEntry[] entries =
            [new(new TermEntryId(3), Entry("manager")), new(new TermEntryId(1), Entry("file")), new(new TermEntryId(3), Entry("folder"))];
        var text = Text("the file manager folder");

        var of = TermIndex.Of(entries);
        var with = entries.Aggregate(TermIndex.Empty, (index, e) => index.With(e.Id, e.Entry));

        Assert.Equal(2, of.Count);
        Assert.Equal([1L, 3L], of.Entries().Select(e => e.Id.Value));
        Assert.Equal(with.Entries(), of.Entries());
        Assert.Equal(with.Find(text), of.Find(text));
    }

    [Fact]
    public void WithRange_IsRepeatedWith_AndOf()
    {
        StoredTermEntry[] entries =
            [new(new TermEntryId(3), Entry("manager")), new(new TermEntryId(1), Entry("file")), new(new TermEntryId(3), Entry("folder"))];
        var start = Index(["file manager"]);
        var text = Text("the file manager folder");

        var range = start.WithRange(entries);
        var with = entries.Aggregate(start, (index, e) => index.With(e.Id, e.Entry));

        Assert.Equal(with.Entries(), range.Entries());
        Assert.Equal(with.Find(text), range.Find(text));
        Assert.Equal(TermIndex.Of(with.Entries()).Find(text), range.Find(text));
        Assert.Equal(1, start.Count);
    }

    [Fact]
    public void WithRange_OfNothing_IsTheSameIndex()
    {
        var index = Index(["file"]);

        Assert.Same(index, index.WithRange([]));
    }

    // An id set twice in one batch keeps only its last entry: the first one's term isn't found any more.
    [Fact]
    public void AnIdSetTwiceInOneBatch_KeepsOnlyTheLastEntry()
    {
        var file = new StoredTermEntry(new TermEntryId(1), Entry("file"));
        var files = new StoredTermEntry(new TermEntryId(1), new TermEntry([new Term("file manager"), new Term("folder")], [], null, Monday, null));

        foreach (var index in new[] { TermIndex.Of([file, files]), TermIndex.Empty.WithRange([file, files]), Index(["file"]).WithRange([files, file, files]) })
        {
            var hit = Assert.Single(index.Find(Text("the file manager file folder")));
            Assert.Equal([(0, 4, 12), (1, 22, 6)], hit.Occurrences.Select(o => (o.Term, o.Start, o.Length)));
        }
    }

    // Terms with punctuation are filed under their characters up to the first space, so many starting alike (.NET-like
    // names, e- words) are each tried only where the text has their characters.
    [Fact]
    public void TermsWithPunctuationStartingAlike_AreFoundByTheirOwnCharacters()
    {
        var index = Index([".alpha"], [".beta"], ["e-mail"], ["e-book"], ["e-book reader"], ["e"]);

        var hits = index.Find(Text("Read .beta e-books on an e-book reader."));

        Assert.Equal([2, 4, 6, 5], hits.Select(h => h.Id.Value));
        Assert.Equal([(5, 5), (11, 6), (11, 1), (25, 13)], hits.Select(h => (h.Occurrences[0].Start, h.Occurrences[0].Length)));
        Assert.Equal([(11, 6), (25, 6)], hits[1].Occurrences.Select(o => (o.Start, o.Length)));
    }

    // Reading the entries may fail (a damaged termbase file, cancelling): building the index throws that, as it is.
    [Fact]
    public void Of_ThrowsWhatReadingTheEntriesThrew()
    {
        static IEnumerable<StoredTermEntry> Failing()
        {
            for (var i = 1; i <= 10_000; i++)
                yield return new StoredTermEntry(new TermEntryId(i), Entry($"term {i}"));
            throw new InvalidDataException("damaged");
        }

        Assert.Equal("damaged", Assert.Throws<InvalidDataException>(() => TermIndex.Of(Failing())).Message);
        Assert.Throws<OperationCanceledException>(() => TermIndex.Empty.WithRange(Failing().Select(e => e.Id.Value < 5_000 ? e : throw new OperationCanceledException())));
    }

    // The check's records are made once per entry by each term source, and kept by the source, not by the index: a
    // record costs nearly as much as its entry in the index, so one kept for every entry ever found would grow past the
    // memory budget while the termbase is open.
    [Fact]
    public void TheTermSource_KeepsEachEntrysRecord_AndTheIndexDoesnt()
    {
        var index = Index(["file"], ["folder"]);
        var text = Text("Move the file to the folder.");

        var terms = index.ToTermSource("client");
        var first = terms.Find(text);
        Assert.Equal(2, first.Count);
        Assert.All(first.Zip(terms.Find(text)), pair => Assert.Same(pair.First, pair.Second));
        Assert.NotSame(first[0], index.ToTermSource("other").Find(text)[0]);

        // A later snapshot's source takes over the previous source's records, except an entry changed since, and only
        // for the same termbase.
        var later = index.With(new TermEntryId(3), Entry("disk")).With(new TermEntryId(2), Entry("folder", "mappa"));
        var next = later.ToTermSource("client", terms).Find(text);
        Assert.Same(first[0], next[0]);
        Assert.NotSame(first[1], next[1]);
        Assert.Equal(["mappa"], next[1].Translations);
        Assert.NotSame(first[0], later.ToTermSource("client").Find(text)[0]);
        Assert.NotSame(first[0], later.ToTermSource("other", terms).Find(text)[0]);

        var records = RecordsOfASourceDropped(index, text);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(2, records.Length);
        Assert.All(records, r => Assert.False(r.TryGetTarget(out _)));
        GC.KeepAlive(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TermRecord>[] RecordsOfASourceDropped(TermIndex index, QaText text) =>
        [.. index.ToTermSource("client").Find(text).Select(r => new WeakReference<TermRecord>(r))];

    // Many terms filed under one key (a common first word) don't copy that key's bucket per term.
    [Fact]
    public void ManyTermsUnderOneKey_AreIndexedAndRemovedInLinearTime()
    {
        var entries = Enumerable.Range(0, 20_000)
            .Select(i => new StoredTermEntry(new TermEntryId(i + 1), Entry($"file {i}")))
            .ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var index = TermIndex.Of(entries).WithoutRange(entries.Where((_, i) => i % 2 == 0).Select(e => e.Id));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"{clock.Elapsed}");
        var rest = TermIndex.Of(entries.Where((_, i) => i % 2 == 1));
        Assert.Equal(rest.Entries(), index.Entries());
        Assert.Equal([2L], index.Find(Text("file 1")).Select(h => h.Id.Value));
        Assert.Empty(index.Find(Text("file 0")));
    }

    // ---------------------------------------------------------------- entries

    [Fact]
    public void AnEntry_RefusesWhatIsntOne()
    {
        Assert.Throws<ArgumentException>(() => new TermEntry([], [], null, Monday, null));
        Assert.Throws<ArgumentNullException>(() => new TermEntry(null!, [], null, Monday, null));
        Assert.Throws<ArgumentNullException>(() => new TermEntry([new Term("a")], null!, null, Monday, null));
        Assert.Throws<ArgumentException>(() => new TermEntry([null!], [], null, Monday, null));
        Assert.Throws<ArgumentException>(() => new TermEntry([new Term("a")], [null!], null, Monday, null));
        Assert.Throws<ArgumentException>(() => Entry("a") with { Source = [] });
        Assert.Throws<ArgumentNullException>(() => new Term(null!));
        Assert.Throws<ArgumentNullException>(() => new Term("a") with { Text = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Term("a", (TermStatus)17));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Term("a") with { Status = (TermStatus)(-1) });
    }

    [Fact]
    public void EqualEntries_AreEqual()
    {
        var a = new TermEntry([new Term("file") { Note = "noun" }], [new Term("fájl", TermStatus.Preferred)], "n", Monday, "me");
        var b = new TermEntry([new Term("file") { Note = "noun" }], [new Term("fájl", TermStatus.Preferred)], "n", Monday, "me");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Target = [new Term("fájl")] });
    }

    // The lists are copied: changing the one passed in doesn't change the entry.
    [Fact]
    public void AnEntry_KeepsItsOwnLists()
    {
        var source = new List<Term> { new("file") };
        var entry = new TermEntry(source, [], null, Monday, null);

        source.Add(new Term("folder"));

        Assert.Single(entry.Source);
    }

    [Fact]
    public void ToRecord_PutsPreferredTermsFirst_AndForbiddenTranslationsApart()
    {
        var entry = new TermEntry(
            [new Term("dir", TermStatus.Forbidden), new Term("folder", TermStatus.Preferred), new Term("map"), new Term("directory", TermStatus.Preferred)],
            [new Term("mappa", TermStatus.Admitted), new Term("dosszié", TermStatus.Forbidden), new Term("könyvtár", TermStatus.Preferred), new Term("tár")],
            null, Monday, null);
        var origin = TermOrigin.OfTermbase("client");

        var record = entry.ToRecord(origin);

        Assert.Equal(["folder", "directory", "dir", "map"], record.Terms);
        Assert.Equal(["mappa", "könyvtár", "tár"], record.Translations);
        Assert.Equal(["dosszié"], record.ForbiddenTranslations);
        Assert.Same(origin, record.Origin);
        Assert.Null(record.MarkerId);
    }

    // ---------------------------------------------------------------- the term source

    [Theory]
    [InlineData("Delete the document.", true)]
    [InlineData("Delete the files.", false)]
    public void TheTermSource_IsCheckedByTheTerminologyCheck(string target, bool reported)
    {
        var issues = Check(Unit(Content("Töröld a fájlt."), Content(target)), Entry("fájl", "file"));

        Assert.Equal(reported ? ["The termbase “client” translates “fájl” as “file”, which isn't in the target."] : [], issues);
    }

    [Fact]
    public void OnlyAForbiddenTranslationInTheTarget_IsReported()
    {
        var entry = new TermEntry([new Term("fájl")], [new Term("file"), new Term("document", TermStatus.Forbidden)], null, Monday, null);

        var issue = Assert.Single(Check(Unit(Content("Töröld a fájlt."), Content("Delete the document.")), entry));

        Assert.Equal("The termbase “client” translates “fájl” as “file”, which isn't in the target.", issue);
    }

    [Fact]
    public void ATermOnlyInTheSourcesEquiv_IsntChecked() =>
        Assert.Empty(Check(Unit(Content("Click ", Ph("fájl"), "."), Content("Click.")), Entry("fájl", "file")));

    [Fact]
    public void ATranslationOnlyInTheTargetsEquiv_DoesntCount() =>
        Assert.Single(Check(Unit(Content("Töröld a fájlt."), Content("Delete ", Ph("file"), ".")), Entry("fájl", "file")));

    // ---------------------------------------------------------------- helpers

    private static TermEntry Entry(string term, params string[] translations) =>
        new([new Term(term)], [.. translations.Select(t => new Term(t))], null, Monday, null);

    // An entry per list of source terms, with ids 1, 2, …
    private static TermIndex Index(params string[][] entries) =>
        TermIndex.Of(entries.Select((terms, i) => new StoredTermEntry(new TermEntryId(i + 1), new TermEntry([.. terms.Select(t => new Term(t))], [], null, Monday, null))));

    private static IReadOnlyList<TermHit> Find(string text, string term) => Index([term]).Find(Text(text));

    // Every occurrence of the one hit; none if nothing's found.
    private static List<(int, int)> Places(IReadOnlyList<TermHit> hits) =>
        [.. hits.SelectMany(h => h.Occurrences).Select(o => (o.Start, o.Length))];

    private static QaText Text(string text) => QaText.Of(InlineContent.FromText(text));

    private static InlineContent Content(params object[] parts) =>
        new(parts.Select(p => p is string s ? new InlineText(s) : (InlineElement)p));

    private static InlineCode Ph(string? equiv = null) => new($"ph{Guid.NewGuid():N}", InlineCodeKind.Standalone) { Equiv = equiv };

    private static TranslationUnit Unit(InlineContent source, InlineContent target)
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = source, Target = target });
        return unit;
    }

    private static List<string> Check(TranslationUnit unit, params TermEntry[] entries)
    {
        var index = TermIndex.Of(entries.Select((e, i) => new StoredTermEntry(new TermEntryId(i + 1), e)));
        return [.. QaChecker.Check(unit, terms: index.ToTermSource("client")).Where(i => i.Kind == QaIssueKind.Terminology).Select(i => i.Message)];
    }
}
