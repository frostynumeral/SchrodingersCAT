using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Tests;

public class ContentRunsTests
{
    [Theory]
    [InlineData("{0}", "xlf:b", "fmt", "{0}")] // disp wins
    [InlineData(null, "xlf:b", "fmt", "b")]
    [InlineData(null, "xlf:i", "fmt", "i")]
    [InlineData(null, "xlf:lb", "fmt", "↵")]
    [InlineData(null, null, "link", "link")]
    [InlineData(null, null, "image", "img")]
    [InlineData(null, null, "fmt", "7")] // too generic: the id
    [InlineData(null, "acme:x", "ui", "7")]
    [InlineData("", null, null, "7")]
    public void Label_PrefersDispThenTypeThenId(string? disp, string? subType, string? type, string expected)
    {
        var code = new InlineCode("7", InlineCodeKind.Standalone) { Disp = disp, SubType = subType, Type = type };

        Assert.Equal(expected, ContentRuns.Label(code));
    }

    [Fact]
    public void Build_TurnsCodesIntoChipsAndMergesText()
    {
        var unit = Unit(new InlineContent(
        [
            new InlineText("Press "),
            new InlineCode("1", InlineCodeKind.Opening) { SubType = "xlf:b", DataRef = "d1" },
            new InlineText("OK"),
            new InlineCode("1", InlineCodeKind.Closing) { SubType = "xlf:b", DataRef = "d2" },
            new InlineMarker("m1", InlineMarkerKind.Opening),
            new InlineText(" now"),
            new InlineMarker("m1", InlineMarkerKind.Closing),
            new InlineText("."),
            new InlineCode("2", InlineCodeKind.Standalone) { Disp = "{0}", Equiv = "Ann", CanDelete = false },
        ]));
        unit.OriginalData["d1"] = "<b class=\"warn\">";
        unit.OriginalData["d2"] = "</b>";

        var runs = Build(unit, unit.Segments[0].Source);

        Assert.Equal(6, runs.Count);
        Assert.Equal(new TextRun("Press ", AnnotationStyle.None), runs[0]);
        var open = Assert.IsType<ChipRun>(runs[1]);
        Assert.Equal(("1", InlineCodeKind.Opening, "⟨b", false), (open.CodeId, open.Kind, open.Text, open.IsProtected));
        Assert.Equal("Start of code 1\n<b class=\"warn\">", open.ToolTip);
        Assert.Equal("b⟩", Assert.IsType<ChipRun>(runs[3]).Text);
        Assert.Equal("End of code 1\n</b>", Assert.IsType<ChipRun>(runs[3]).ToolTip);

        // A plain annotation doesn't change the text's look, so the text around it is one run.
        Assert.Equal(new TextRun(" now.", AnnotationStyle.None), runs[4]);

        var placeholder = Assert.IsType<ChipRun>(runs[5]);
        Assert.Equal("{0}", placeholder.Text);
        Assert.True(placeholder.IsProtected);
        Assert.Equal("Code 2\nReads as: Ann\nCan't be deleted.", placeholder.ToolTip);
    }

    [Fact]
    public void Build_DescribesFixedOrderSequencesAndCopyRestrictions()
    {
        var unit = Unit(new InlineContent(
        [
            new InlineCode("1", InlineCodeKind.Standalone) { CanReorder = CanReorder.FirstNo },
            new InlineCode("2", InlineCodeKind.Standalone) { CanReorder = CanReorder.No },
            new InlineCode("3", InlineCodeKind.Standalone) { CanCopy = false },
        ]));

        var chips = Build(unit, unit.Segments[0].Source).Cast<ChipRun>().ToList();

        Assert.All(chips.Take(2), c => Assert.True(c.IsProtected));
        Assert.Equal("Code 1\nCan't be deleted.\nCan't be copied.\nPart of a fixed-order sequence.", chips[0].ToolTip);
        Assert.False(chips[2].IsProtected);
        Assert.Equal("Code 3\nCan't be copied.", chips[2].ToolTip);
    }

    [Fact]
    public void Build_ACopyInTheTargetIsDeletable()
    {
        var unit = Unit(new InlineContent([new InlineCode("1", InlineCodeKind.Standalone) { CanDelete = false }]));
        var target = new InlineContent(
        [
            new InlineCode("1", InlineCodeKind.Standalone) { CanDelete = false },
            new InlineCode("c1", InlineCodeKind.Standalone) { CopyOf = "1" },
        ]);

        var chips = Build(unit, target).Cast<ChipRun>().ToList();

        Assert.True(chips[0].IsProtected);
        Assert.False(chips[1].IsProtected);
        Assert.Equal("Code c1\nCopy of code 1", chips[1].ToolTip);
    }

    [Fact]
    public void Build_StylesAnnotatedText()
    {
        var unit = Unit(new InlineContent(
        [
            new InlineMarker("t", InlineMarkerKind.Opening) { Type = "term" },
            new InlineText("cat"),
            new InlineMarker("n", InlineMarkerKind.Opening) { Translate = false },
            new InlineText("CAT"),
            new InlineMarker("t", InlineMarkerKind.Closing),
            new InlineText("Tool"),
            new InlineMarker("n", InlineMarkerKind.Closing),
            new InlineMarker("c", InlineMarkerKind.Opening) { Type = "comment", Value = "Check this" },
            new InlineText("!"),
            new InlineMarker("c", InlineMarkerKind.Closing),
        ]));

        var runs = Build(unit, unit.Segments[0].Source);

        Assert.Equal(
            [
                new TextRun("cat", AnnotationStyle.Term),
                new TextRun("CAT", AnnotationStyle.Term | AnnotationStyle.NoTranslate),
                new TextRun("Tool", AnnotationStyle.NoTranslate),
                new TextRun("!", AnnotationStyle.Comment),
            ],
            runs);
    }

    [Fact]
    public void SegmentViewModel_CarriesAnnotationsAcrossSegments()
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineText("A "), new InlineMarker("t", InlineMarkerKind.Opening) { Type = "term" }, new InlineText("big")]),
        });
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineText("cat"), new InlineMarker("t", InlineMarkerKind.Closing), new InlineText(".")]),
        });

        var second = new SegmentViewModel(unit, unit.Segments[1], () => { });

        Assert.Equal([new TextRun("cat", AnnotationStyle.Term), new TextRun(".", AnnotationStyle.None)], second.SourceRuns);
    }

    [Fact]
    public void SegmentViewModel_TargetRunsAreEmptyWithoutATarget()
    {
        var unit = Unit(new InlineContent([new InlineCode("1", InlineCodeKind.Standalone)]));
        var segment = new SegmentViewModel(unit, unit.Segments[0], () => { });

        Assert.Empty(segment.TargetRuns);

        unit.Segments[0].Target = InlineContent.Empty;
        Assert.Empty(segment.TargetRuns);
    }

    private static TranslationUnit Unit(InlineContent source)
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = source });
        return unit;
    }

    private static IReadOnlyList<ContentRun> Build(TranslationUnit unit, InlineContent content) =>
        ContentRuns.Build(content, unit, new InlineCodeHints(unit), []);
}
