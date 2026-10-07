using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class SourceDiffTests
{
    [Fact]
    public void EqualSources_AreOnePart() =>
        Assert.Equal([new DiffPart(new InlineText("Press OK to continue."), DiffKind.Same)], SourceDiff.Compare(Text("Press OK to continue."), Text("Press OK to continue.")));

    [Fact]
    public void ADifferentWord_IsRemovedThenAdded() =>
        Assert.Equal(
            [("Save the ", DiffKind.Same), ("file", DiffKind.Removed), ("document", DiffKind.Added), (" now.", DiffKind.Same)],
            Parts(SourceDiff.Compare(Text("Save the document now."), Text("Save the file now."))));

    [Fact]
    public void MissingAndExtraWords_AreAddedAndRemoved() =>
        Assert.Equal(
            [("Open ", DiffKind.Same), ("the ", DiffKind.Removed), ("file", DiffKind.Same), (" again", DiffKind.Added), (".", DiffKind.Same)],
            Parts(SourceDiff.Compare(Text("Open file again."), Text("Open the file."))));

    // The score treats these as (nearly) the same; the translator still has to change them in the translation.
    [Theory]
    [InlineData("Copy 3 files.", "Copy 4 files.", "3", "4")]
    [InlineData("Press ok.", "Press OK.", "ok", "OK")]
    [InlineData("Save it.", "Save it!", ".", "!")]
    public void NumbersCaseAndPunctuation_AreDifferences(string match, string current, string removed, string added)
    {
        var parts = Parts(SourceDiff.Compare(Text(current), Text(match)));

        Assert.Equal(removed, Assert.Single(parts, p => p.Kind == DiffKind.Removed).Text);
        Assert.Equal(added, Assert.Single(parts, p => p.Kind == DiffKind.Added).Text);
    }

    [Fact]
    public void Whitespace_IsNotADifference()
    {
        Assert.Equal([new DiffPart(new InlineText("Press OK."), DiffKind.Same)], SourceDiff.Compare(Text("Press  OK.\n"), Text("Press OK.")));
        Assert.Equal([new DiffPart(new InlineText(" Press OK. "), DiffKind.Same)], SourceDiff.Compare(Text("Press OK."), Text(" Press OK. ")));
    }

    [Fact]
    public void ComposedAndDecomposedCharacters_AreTheSame() =>
        Assert.All(SourceDiff.Compare(Text("Café au lait"), Text("Café au lait")), p => Assert.Equal(DiffKind.Same, p.Kind));

    [Fact]
    public void Codes_CompareByKind_AndKeepTheirSide()
    {
        var matchCode = new InlineCode("7", InlineCodeKind.Standalone);
        var currentCode = new InlineCode("1", InlineCodeKind.Standalone);
        var currentPair = new InlineCode("2", InlineCodeKind.Opening);

        var parts = SourceDiff.Compare(
            new InlineContent([new InlineText("Hello "), currentCode, new InlineText(" "), currentPair, new InlineText("world")]),
            new InlineContent([new InlineText("Hello "), matchCode, new InlineText(" world")]));

        Assert.Equal(
            [
                new DiffPart(new InlineText("Hello "), DiffKind.Same),
                new DiffPart(matchCode, DiffKind.Same),
                new DiffPart(new InlineText(" "), DiffKind.Same),
                new DiffPart(currentPair, DiffKind.Added),
                new DiffPart(new InlineText("world"), DiffKind.Same),
            ],
            parts);
    }

    [Fact]
    public void Annotations_AreLeftOut()
    {
        var parts = SourceDiff.Compare(
            Text("A term."),
            new InlineContent([new InlineText("A "), new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" }, new InlineText("term"), new InlineMarker("m1", InlineMarkerKind.Closing), new InlineText(".")]));

        Assert.Equal([new DiffPart(new InlineText("A term."), DiffKind.Same)], parts);
    }

    [Fact]
    public void BeyondMaxCells_TheMiddleIsRemovedThenAdded()
    {
        var words = (int)Math.Sqrt(SourceDiff.MaxCells) + 1;
        var match = "Start " + string.Join(" ", Enumerable.Range(0, words).Select(i => $"a{i}")) + " end";
        var current = "Start " + string.Join(" ", Enumerable.Range(0, words).Select(i => $"b{i}")) + " end";

        var parts = Parts(SourceDiff.Compare(Text(current), Text(match)));

        Assert.Equal([DiffKind.Same, DiffKind.Removed, DiffKind.Added, DiffKind.Same], parts.Select(p => p.Kind));
        Assert.Equal("Start ", parts[0].Text);
        Assert.Equal(" end", parts[3].Text);
    }

    [Fact]
    public void EmptySources_HaveNoParts() =>
        Assert.Empty(SourceDiff.Compare(InlineContent.Empty, InlineContent.Empty));

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private static List<(string Text, DiffKind Kind)> Parts(IReadOnlyList<DiffPart> parts) =>
        [.. parts.Select(p => (Assert.IsType<InlineText>(p.Element).Text, p.Kind))];
}
