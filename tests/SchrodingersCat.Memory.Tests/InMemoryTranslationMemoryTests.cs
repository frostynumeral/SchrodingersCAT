using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class InMemoryTranslationMemoryTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lookup_ReturnsMatchesAboveTheThreshold_BestFirst()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(Entry("Save the file.", "Mentsd a fájlt."));
        tm.Add(Entry("Save the file now.", "Mentsd most a fájlt."));
        tm.Add(Entry("Delete everything.", "Törölj mindent."));

        var matches = tm.Lookup(Text("Save the file."));

        Assert.Equal([(100, "Mentsd a fájlt."), (80, "Mentsd most a fájlt.")], matches.Select(m => (m.Score, m.Entry.Target.ToPlainText())));
        Assert.True(matches[0].IsExact);
        Assert.False(matches[1].IsExact);
    }

    [Fact]
    public void Lookup_KeepsAtMostTheMaximum_NewestFirstAmongEqualScores()
    {
        var tm = new InMemoryTranslationMemory();
        for (var day = 0; day < 7; day++)
            tm.Add(Entry("Save the file.", $"Fordítás {day}.", Monday.AddDays(day)));

        var matches = tm.Lookup(Text("Save the file."));

        Assert.Equal(InMemoryTranslationMemory.DefaultMaxMatches, matches.Count);
        Assert.Equal("Fordítás 6.", matches[0].Entry.Target.ToPlainText());
    }

    [Fact]
    public void AnExactMatchBetweenTheSameNeighbours_Is101()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(Entry("OK", "OK") with { PreviousSource = "Save the file?", NextSource = "Cancel" });
        tm.Add(Entry("OK", "Rendben") with { PreviousSource = "Delete it?", NextSource = "Cancel", Changed = Monday.AddDays(1) });

        var matches = tm.Lookup(Text("OK"), "Save  the file?", "Cancel");

        Assert.Equal([(101, "OK"), (100, "Rendben")], matches.Select(m => (m.Score, m.Entry.Target.ToPlainText())));
    }

    [Fact]
    public void ALoneSegment_IsNeverAContextMatch()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(Entry("OK", "Rendben"));

        Assert.Equal(100, tm.Lookup(Text("OK")).Single().Score);
    }

    [Fact]
    public void AddingTheSamePairAgain_ReplacesIt_AnotherTranslationIsKept()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(Entry("Save", "Mentés"));
        tm.Add(Entry("Save", "Mentés", Monday.AddDays(1)));
        tm.Add(Entry("Save", "Mentsd"));

        Assert.Equal([("Mentés", Monday.AddDays(1)), ("Mentsd", Monday)], tm.Entries().Select(e => (e.Target.ToPlainText(), e.Changed)));
    }

    [Fact]
    public void Remove_TakesOutOnlyThatPair()
    {
        var tm = new InMemoryTranslationMemory();
        tm.AddRange([Entry("Save", "Mentés"), Entry("Save", "Mentsd"), Entry("Open", "Megnyitás")]);

        Assert.True(tm.Remove(Entry("save", "Mentés", Monday.AddDays(3)) with { Source = Text("Save") }));
        Assert.False(tm.Remove(Entry("Save", "Mentés")));
        tm.Add(Entry("Save", "Mentés", Monday.AddDays(1)));

        Assert.Equal(["Mentsd", "Megnyitás", "Mentés"], tm.Entries().Select(e => e.Target.ToPlainText()));
        Assert.Equal(["Mentés", "Mentsd"], tm.Lookup(Text("Save")).Select(m => m.Entry.Target.ToPlainText()));
    }

    // Re-confirming a corrected segment: its old pair goes, unless it came from another document.
    [Fact]
    public void Replace_RemovesThePreviousPairOnlyIfStoredFromTheSameDocument()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(Entry("Save", "Mentéss") with { Document = "a.xlf" });
        tm.Add(Entry("Open", "Nyiss") with { Document = "other.xlf" });

        tm.Replace(Entry("Save", "Mentéss") with { Document = "a.xlf" }, Entry("Save", "Mentés") with { Document = "a.xlf" });
        tm.Replace(Entry("Open", "Nyiss") with { Document = "a.xlf" }, Entry("Open", "Megnyitás") with { Document = "a.xlf" });
        tm.Replace(Entry("Close", "Zárj") with { Document = "a.xlf" }, Entry("Close", "Bezárás") with { Document = "a.xlf" }); // nothing to replace

        Assert.Equal(["Nyiss", "Mentés", "Megnyitás", "Bezárás"], tm.Entries().Select(e => e.Target.ToPlainText()));
    }

    private static TmEntry Entry(string source, string target, DateTimeOffset? changed = null) =>
        new(Text(source), Text(target)) { Changed = changed ?? Monday };

    private static InlineContent Text(string text) => InlineContent.FromText(text);
}
