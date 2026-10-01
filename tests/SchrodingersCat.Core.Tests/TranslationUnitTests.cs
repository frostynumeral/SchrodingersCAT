using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

public class TranslationUnitTests
{
    private static readonly InlineCode Placeholder = new("1", InlineCodeKind.Standalone) { Disp = "{0}" };

    [Fact]
    public void Source_ConcatenatesSegmentsAndKeepsCodes()
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = new InlineContent([new InlineText("Hello "), Placeholder, new InlineText(".")]) });
        unit.Segments.Add(new Segment { Source = InlineContent.FromText(" "), IsIgnorable = true });
        unit.Segments.Add(new Segment { Source = InlineContent.FromText("Bye.") });

        Assert.Equal(new InlineContent([new InlineText("Hello "), Placeholder, new InlineText(". Bye.")]), unit.Source);
    }

    [Fact]
    public void Target_FallsBackToSourceForUntranslatedSegments()
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineText("Hello "), Placeholder]),
            Target = new InlineContent([new InlineText("Szia "), Placeholder]),
        });
        unit.Segments.Add(new Segment { Source = InlineContent.FromText("!") });

        Assert.Equal(new InlineContent([new InlineText("Szia "), Placeholder, new InlineText("!")]), unit.Target);
    }
}
