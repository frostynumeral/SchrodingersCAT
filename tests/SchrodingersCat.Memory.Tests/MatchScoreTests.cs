using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class MatchScoreTests
{
    [Fact]
    public void TheSameSource_Is100() =>
        Assert.Equal(100, MatchScore.Compare(Text("Press OK to continue."), Text("Press OK to continue.")));

    [Theory]
    [InlineData("Press OK to continue.", "press ok to continue.")] // case
    [InlineData("Press OK to continue.", "Press  OK to continue. ")] // whitespace
    [InlineData("Café", "Cafe\u0301 ")] // NFC makes the é equal; the space doesn't go away
    public void OnlyCaseOrWhitespace_Is99(string a, string b) =>
        Assert.Equal(99, MatchScore.Compare(Text(a), Text(b)));

    [Fact]
    public void ComposedAndDecomposedCharacters_AreTheSame() =>
        Assert.Equal(100, MatchScore.Compare(Text("Café"), Text("Café")));

    [Fact]
    public void OneWordOfTen_Is90() =>
        Assert.Equal(90, MatchScore.Compare(Text("one two three four five six seven eight nine ten"), Text("one two three four five six seven eight nine eleven")));

    [Fact]
    public void PunctuationCountsAsAWord() =>
        Assert.Equal(75, MatchScore.Compare(Text("Save the file."), Text("Save the file!")));

    [Fact]
    public void DifferentNumbers_CostAPointEach_NotAWord() =>
        Assert.Equal(98, MatchScore.Compare(Text("Copy 3 of 10 files."), Text("Copy 4 of 12 files.")));

    [Fact]
    public void CodeIds_DontMatter()
    {
        var a = new InlineContent([new InlineText("Press "), Code("1", InlineCodeKind.Opening), new InlineText("OK"), Code("1", InlineCodeKind.Closing)]);
        var b = new InlineContent([new InlineText("Press "), Code("b7", InlineCodeKind.Opening), new InlineText("OK"), Code("b7", InlineCodeKind.Closing)]);

        Assert.Equal(100, MatchScore.Compare(a, b));
    }

    [Fact]
    public void MissingCodes_CostTwoPointsEach()
    {
        var withCodes = new InlineContent([new InlineText("Press "), Code("1", InlineCodeKind.Opening), new InlineText("OK"), Code("1", InlineCodeKind.Closing)]);

        Assert.Equal(96, MatchScore.Compare(withCodes, Text("Press OK")));
    }

    [Fact]
    public void Annotations_AreIgnored()
    {
        var annotated = new InlineContent([new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" }, new InlineText("file"), new InlineMarker("m1", InlineMarkerKind.Closing)]);

        Assert.Equal(100, MatchScore.Compare(annotated, Text("file")));
    }

    [Fact]
    public void NothingInCommon_Is0() =>
        Assert.Equal(0, MatchScore.Compare(Text("alpha beta"), Text("gamma delta")));

    [Fact]
    public void EmptySources_AreEqual() =>
        Assert.Equal(100, MatchScore.Compare(InlineContent.Empty, Text("")));

    // Regression (found by the property test): Normalize threw on code points that <cp> can bring in.
    [Fact]
    public void InvalidCodePoints_AreComparedAsTheyAre()
    {
        Assert.Equal(100, MatchScore.Compare(Text("a\uD800b\uFFFF"), Text("a\uD800b\uFFFF")));
        Assert.Equal(99, MatchScore.Compare(Text("a\uD800b"), Text("A\uD800b")));
        Assert.True(MatchScore.SameContext("x\uFFFF  y", "x\uFFFF y"));
    }

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private static InlineCode Code(string id, InlineCodeKind kind) => new(id, kind);
}
