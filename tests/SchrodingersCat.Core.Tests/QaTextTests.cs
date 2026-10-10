using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

/// <summary><see cref="QaText"/>'s mapping of its text back to the content (step 2 of <c>docs/design/termbase.md</c>).</summary>
public class QaTextTests
{
    [Fact]
    public void SpansOf_InsideOneText_IsOneSpan()
    {
        var text = QaText.Of(Content("Open the file."));

        Assert.Equal([new TextSpan(0, 9, 4)], text.SpansOf(9, 4));
    }

    // Codes are covered too, so a range across one gives the code in the middle.
    [Fact]
    public void SpansOf_AcrossACode_CoversItsElements()
    {
        var text = QaText.Of(Content("file", new InlineCode("1", InlineCodeKind.Standalone), "manager"));

        Assert.Equal([new TextSpan(0, 2, 2), new TextSpan(1, 0, 1), new TextSpan(2, 0, 3)], text.SpansOf(2, 6));
    }

    [Fact]
    public void SpansOf_EndingInsideAnEquiv_CoversPartOfIt()
    {
        var text = QaText.Of(Content("Press ", new InlineCode("1", InlineCodeKind.Standalone) { Equiv = "OK" }, " now"));

        Assert.Equal([new TextSpan(0, 4, 2), new TextSpan(1, 0, 1)], text.SpansOf(4, 3));
    }

    // An annotation's half has no text: a range across it is two spans of the texts on either side.
    [Fact]
    public void SpansOf_AcrossAMarker_IsTwoTextSpans()
    {
        var text = QaText.Of(Content("the file", new InlineMarker("m1", InlineMarkerKind.Opening), "manager"));

        Assert.Equal([new TextSpan(0, 4, 4), new TextSpan(2, 0, 3)], text.SpansOf(4, 7));
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(5, -2)]
    [InlineData(14, 3)]
    [InlineData(-5, 3)]
    [InlineData(20, 5)]
    public void SpansOf_AnEmptyRangeOrOneOutsideTheText_CoversNothing(int start, int length) =>
        Assert.Empty(QaText.Of(Content("Open the file.")).SpansOf(start, length));

    [Theory]
    [InlineData(-2, 6, 0, 0, 4)]
    [InlineData(10, 100, 0, 10, 4)]
    [InlineData(0, int.MaxValue, 0, 0, 14)]
    public void SpansOf_ARangeReachingOutside_IsCutToTheText(int start, int length, int element, int spanStart, int spanLength) =>
        Assert.Equal([new TextSpan(element, spanStart, spanLength)], QaText.Of(Content("Open the file.")).SpansOf(start, length));

    [Fact]
    public void SpansOf_StartsWhereSpanOfDoes()
    {
        var text = QaText.Of(Content("ab", new InlineCode("1", InlineCodeKind.Opening), "cd", new InlineCode("1", InlineCodeKind.Closing), "ef"));

        for (var start = 0; start < text.Text.Length; start++)
        {
            for (var length = 1; start + length <= text.Text.Length + 1; length++)
                Assert.Equal(text.SpanOf(start, length), text.SpansOf(start, length)[0]);
        }
    }

    private static InlineContent Content(params object[] parts) =>
        new(parts.Select(p => p is string s ? new InlineText(s) : (InlineElement)p));
}
