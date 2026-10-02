using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Tests;

public class InlineFormatterTests
{
    [Fact]
    public void Format_ShowsCodesAsPlaceholdersAndHidesMarkers()
    {
        var content = new InlineContent(
        [
            new InlineText("Line"),
            new InlineCode("1", InlineCodeKind.Standalone),
            new InlineCode("2", InlineCodeKind.Opening),
            new InlineMarker("m1", InlineMarkerKind.Opening),
            new InlineText("bold"),
            new InlineMarker("m1", InlineMarkerKind.Closing),
            new InlineCode("2", InlineCodeKind.Closing),
        ]);

        Assert.Equal("Line⟨1/⟩⟨2⟩bold⟨/2⟩", InlineFormatter.Format(content));
    }
}
