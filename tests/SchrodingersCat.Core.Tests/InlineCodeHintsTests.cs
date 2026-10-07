using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

public class InlineCodeHintsTests : Xliff20SerializerTestBase
{
    [Fact]
    public async Task SourceCodes_ListsEveryHalfInDocumentOrder_IgnorablesIncluded()
    {
        var hints = await HintsAsync("""
            <segment><source><pc id="1">Bold</pc> text</source></segment>
            <ignorable><source><ph id="2"/></source></ignorable>
            <segment><source>More <ph id="3"/></source></segment>
            """);

        Assert.Equal(
            [("1", InlineCodeKind.Opening), ("1", InlineCodeKind.Closing), ("2", InlineCodeKind.Standalone), ("3", InlineCodeKind.Standalone)],
            hints.SourceCodes.Select(c => (c.Id, c.Kind)));
    }

    [Fact]
    public async Task Lookups_FindCodesByIdAndKind()
    {
        var hints = await HintsAsync("""
            <segment><source><sc id="1"/>Bold</source></segment>
            <segment><source>still bold<ec startRef="1"/> and <ph id="2" disp="{0}"/></source></segment>
            """);

        Assert.True(hints.IsInSource("1"));
        Assert.Equal([InlineCodeKind.Opening, InlineCodeKind.Closing], hints.SourceHalves("1").Select(c => c.Kind));
        Assert.Equal("{0}", hints.SourceCode("2", InlineCodeKind.Standalone)?.Disp);
        Assert.Null(hints.SourceCode("2", InlineCodeKind.Opening));
    }

    [Fact]
    public async Task CodeNotInSource_IsNeitherProtectedNorCopyable()
    {
        var hints = await HintsAsync("""<segment><source>Hello <ph id="1"/></source></segment>""");

        Assert.False(hints.IsInSource("9"));
        Assert.Empty(hints.SourceHalves("9"));
        Assert.Null(hints.SourceCode("9", InlineCodeKind.Standalone));
        Assert.False(hints.IsProtected("9"));
        Assert.False(hints.IsCopyable("9"));
        Assert.Null(hints.SequenceOf("9"));
    }

    [Theory]
    [InlineData("""<ph id="1"/>""", false, true)]
    [InlineData("""<ph id="1" canDelete="no"/>""", true, true)]
    [InlineData("""<ph id="1" canCopy="no"/>""", false, false)]
    [InlineData("""<pc id="1" canDelete="no" canCopy="no">x</pc>""", true, false)]
    public async Task ProtectedAndCopyable_FollowTheAttributes(string code, bool isProtected, bool isCopyable)
    {
        var hints = await HintsAsync($"<segment><source>a {code} b</source></segment>");

        Assert.Equal((isProtected, isCopyable), (hints.IsProtected("1"), hints.IsCopyable("1")));
    }

    [Fact]
    public async Task NonReorderableSequence_IsProtectedAndNotCopyable_WhateverTheAttributesSay()
    {
        var hints = await HintsAsync("""
            <segment><source><ph id="1" canReorder="firstNo"/> of <ph id="2" canReorder="no"/></source></segment>
            <segment><source>and <ph id="3" canReorder="no"/> then <ph id="4"/></source></segment>
            """);

        foreach (var id in (string[])["1", "2", "3"])
        {
            Assert.Equal("1", hints.SequenceOf(id));
            Assert.True(hints.IsProtected(id), id);
            Assert.False(hints.IsCopyable(id), id);
        }

        Assert.Null(hints.SequenceOf("4"));
        Assert.False(hints.IsProtected("4"));
        Assert.True(hints.IsCopyable("4"));
    }

    [Fact]
    public async Task ConsecutiveSequences_AreKeptApart()
    {
        var hints = await HintsAsync("""
            <segment><source><ph id="1" canReorder="firstNo"/><ph id="2" canReorder="no"/><ph id="3" canReorder="firstNo"/><ph id="4" canReorder="no"/></source></segment>
            """);

        Assert.Equal(("1", "1", "3", "3"), (hints.SequenceOf("1"), hints.SequenceOf("2"), hints.SequenceOf("3"), hints.SequenceOf("4")));
    }

    [Fact]
    public async Task LoneFirstNoCode_CountsAsReorderable()
    {
        // Spec: a non-reorderable sequence of one code is equivalent to canReorder="yes".
        var hints = await HintsAsync("""<segment><source><pc id="1" canReorder="firstNo">a</pc> <ph id="2"/></source></segment>""");

        Assert.Null(hints.SequenceOf("1"));
        Assert.False(hints.IsProtected("1"));
        Assert.True(hints.IsCopyable("1"));
    }

    [Fact]
    public void Constructor_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => new InlineCodeHints(null!));

    private async Task<InlineCodeHints> HintsAsync(string unitContent) => new(await ReadUnitAsync(unitContent));
}
