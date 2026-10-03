using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class CodeTransferTests
{
    private static readonly InlineCode BoldTm = new("1", InlineCodeKind.Opening) { SubType = "xlf:b" };
    private static readonly InlineCode BoldTmEnd = new("1", InlineCodeKind.Closing) { SubType = "xlf:b" };
    private static readonly InlineCode Bold = new("b7", InlineCodeKind.Opening) { SubType = "xlf:b", DataRef = "d1" };
    private static readonly InlineCode BoldEnd = new("b7", InlineCodeKind.Closing) { SubType = "xlf:b", DataRef = "d2" };

    [Fact]
    public void TheMatchsCodesBecomeTheCurrentSourcesCodes()
    {
        var entry = new TmEntry(
            Content(new InlineText("Press "), BoldTm, new InlineText("OK"), BoldTmEnd),
            Content(new InlineText("Nyomd meg: "), BoldTm, new InlineText("OK"), BoldTmEnd));

        var result = CodeTransfer.Apply(entry, Content(new InlineText("Press "), Bold, new InlineText("Save"), BoldEnd));

        Assert.Equal(Content(new InlineText("Nyomd meg: "), Bold, new InlineText("OK"), BoldEnd), result.Target);
        Assert.True(result.AllCodesPlaced);
    }

    [Fact]
    public void StandaloneCodesPairInOrder()
    {
        var entry = new TmEntry(
            Content(Ph("a"), new InlineText(" of "), Ph("b")),
            Content(Ph("b"), new InlineText(" / "), Ph("a"))); // the translation reorders them

        var result = CodeTransfer.Apply(entry, Content(Ph("x"), new InlineText(" of "), Ph("y")));

        Assert.Equal(Content(Ph("y"), new InlineText(" / "), Ph("x")), result.Target);
        Assert.True(result.AllCodesPlaced);
    }

    [Fact]
    public void CodesThatDontPairUp_AreLeftOut()
    {
        var entry = new TmEntry(Content(new InlineText("A"), Ph("1"), Ph("2")), Content(new InlineText("Á"), Ph("1"), Ph("2")));

        var fewer = CodeTransfer.Apply(entry, Content(new InlineText("A"), Ph("x")));
        Assert.Equal(Content(new InlineText("Á"), Ph("x")), fewer.Target);
        Assert.False(fewer.AllCodesPlaced);

        var more = CodeTransfer.Apply(entry, Content(new InlineText("A"), Ph("x"), Ph("y"), Ph("z")));
        Assert.Equal(Content(new InlineText("Á"), Ph("x"), Ph("y")), more.Target);
        Assert.False(more.AllCodesPlaced); // z still has to be inserted
    }

    [Fact]
    public void APairWithoutItsClosingHalfInTheCurrentSource_KeepsOnlyTheOpening()
    {
        var entry = new TmEntry(Content(BoldTm, new InlineText("OK"), BoldTmEnd), Content(BoldTm, new InlineText("OK"), BoldTmEnd));

        var result = CodeTransfer.Apply(entry, Content(Bold, new InlineText("OK"))); // the pair closes in the next segment

        Assert.Equal(Content(Bold, new InlineText("OK")), result.Target);
        Assert.False(result.AllCodesPlaced);
    }

    [Fact]
    public void ClosingHalvesOfPairsOpenedElsewhere_PairInOrder()
    {
        var entry = new TmEntry(Content(new InlineText("end"), BoldTmEnd), Content(new InlineText("vége"), BoldTmEnd));

        var result = CodeTransfer.Apply(entry, Content(new InlineText("end"), BoldEnd));

        Assert.Equal(Content(new InlineText("vége"), BoldEnd), result.Target);
        Assert.True(result.AllCodesPlaced);
    }

    [Fact]
    public void AnnotationsAndUnknownCodes_AreLeftOut()
    {
        var entry = new TmEntry(
            Content(new InlineText("file")),
            Content(new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" }, new InlineText("fájl"), new InlineMarker("m1", InlineMarkerKind.Closing), Ph("copy")));

        var result = CodeTransfer.Apply(entry, Content(new InlineText("file")));

        Assert.Equal(Content(new InlineText("fájl")), result.Target);
        Assert.False(result.AllCodesPlaced);
    }

    private static InlineCode Ph(string id) => new(id, InlineCodeKind.Standalone);

    private static InlineContent Content(params InlineElement[] elements) => new(elements);
}
