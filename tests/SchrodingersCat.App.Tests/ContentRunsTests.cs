using SchrodingersCat.App.Editing;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

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

        var second = new SegmentViewModel(new UnitCheck(unit), unit.Segments[1], () => { });

        Assert.Equal([new TextRun("cat", AnnotationStyle.Term), new TextRun(".", AnnotationStyle.None)], second.SourceRuns);
    }

    [Fact]
    public void SegmentViewModel_TargetRunsAreEmptyWithoutATarget()
    {
        var unit = Unit(new InlineContent([new InlineCode("1", InlineCodeKind.Standalone)]));
        var segment = new SegmentViewModel(new UnitCheck(unit), unit.Segments[0], () => { });

        Assert.Empty(segment.TargetRuns);

        unit.Segments[0].Target = InlineContent.Empty;
        Assert.Empty(segment.TargetRuns);
    }

    [Fact]
    public void Slice_CountsAChipAsOnePosition_AndKeepsItsCode()
    {
        var open = new InlineCode("1", InlineCodeKind.Opening);
        var close = new InlineCode("1", InlineCodeKind.Closing);
        var source = new InlineContent([new InlineText("Press "), open, new InlineText("OK"), close, new InlineText(".")]);
        var unit = Unit(source);
        var runs = Build(unit, source);

        Assert.Equal(source, ContentRuns.Slice(runs, 0, 11));
        Assert.Equal(new InlineContent([new InlineText("ss "), open, new InlineText("O")]), ContentRuns.Slice(runs, 3, 8));
        Assert.Equal(InlineContent.Empty, ContentRuns.Slice(runs, 4, 4));
    }

    [Fact]
    public void CodeChoices_AreTheUnitsSourceCodes_ThisSegmentsFirst()
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = new InlineContent([new InlineCode("a", InlineCodeKind.Standalone) { Disp = "{0}" }]) });
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineCode("b", InlineCodeKind.Opening) { SubType = "xlf:b" }, new InlineText("x"), new InlineCode("b", InlineCodeKind.Closing) { SubType = "xlf:b" }]),
            Target = new InlineContent([new InlineCode("b", InlineCodeKind.Opening) { SubType = "xlf:b" }, new InlineText("x"), new InlineCode("b", InlineCodeKind.Closing) { SubType = "xlf:b" }]),
        });

        var choices = CodeChoice.For(new TargetEditModel(unit, unit.Segments[1]));

        Assert.Equal([("b", "⟨b…b⟩", false), ("a", "{0}", true)], choices.Select(c => (c.CodeId, c.Text, c.IsMissing)));
        Assert.StartsWith("Missing from the target.", choices[1].Description, StringComparison.Ordinal);
        Assert.True(choices[1].Matches("{0"));
        Assert.True(choices[1].Matches("A")); // the id, ignoring case
        Assert.False(choices[1].Matches("b"));
    }

    // A match's codes come from another sentence: their chips don't claim this unit's hints. Added codes are this unit's.
    [Fact]
    public void BuildDiff_MarksTheDifferences()
    {
        var current = new InlineCode("1", InlineCodeKind.Standalone) { Disp = "{0}", CanDelete = false };
        var unit = Unit(new InlineContent([new InlineText("Delete "), current, new InlineText(" files?")]));
        var fromMatch = new InlineCode("1", InlineCodeKind.Standalone) { Disp = "%s" };
        var parts = SourceDiff.Compare(unit.Segments[0].Source, new InlineContent([new InlineText("Delete all "), fromMatch, new InlineText(" files?")]));

        var runs = ContentRuns.BuildDiff(parts, unit, new InlineCodeHints(unit));

        Assert.Equal(
            [("Delete ", DiffKind.Same), ("all ", DiffKind.Removed), ("%s", DiffKind.Same), (" files?", DiffKind.Same)],
            runs.Select(r => (r is TextRun t ? t.Text : ((ChipRun)r).Text, r.Difference)));
        var chip = Assert.IsType<ChipRun>(runs[2]);
        Assert.False(chip.IsProtected);
        Assert.Equal("A code in the match", chip.ToolTip);
    }

    [Fact]
    public void BuildDiff_AddedCodesAreThisUnitsChips()
    {
        var current = new InlineCode("1", InlineCodeKind.Standalone) { Disp = "{0}", CanDelete = false };
        var unit = Unit(new InlineContent([new InlineText("Delete "), current]));
        var parts = SourceDiff.Compare(unit.Segments[0].Source, InlineContent.FromText("Delete "));

        var chip = Assert.IsType<ChipRun>(Assert.Single(ContentRuns.BuildDiff(parts, unit, new InlineCodeHints(unit)), r => r is ChipRun));

        Assert.Equal((DiffKind.Added, true, "{0}"), (chip.Difference, chip.IsProtected, chip.Text));
    }

    [Fact]
    public void BuildHighlight_MarksTheHits_AndShowsTheEntrysCodesAsPlainChips()
    {
        var code = new InlineCode("7", InlineCodeKind.Standalone) { Disp = "{0}", CanDelete = false };

        var runs = ContentRuns.BuildHighlight([
            new HighlightPart(new InlineText("Delete "), false), new HighlightPart(new InlineText("files"), true),
            new HighlightPart(code, false), new HighlightPart(new InlineMarker("m1", InlineMarkerKind.Opening), false)]);

        Assert.Equal([("Delete ", false), ("files", true), ("{0}", false)], runs.Select(r => (r is TextRun t ? t.Text : ((ChipRun)r).Text, r.IsHit)));
        Assert.Equal(("A code in the match", false), (((ChipRun)runs[2]).ToolTip, ((ChipRun)runs[2]).IsProtected));
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
