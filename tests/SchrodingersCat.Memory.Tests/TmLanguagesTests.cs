using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class TmLanguagesTests
{
    [Theory]
    [InlineData("en", "hu", "en", "hu", 0)]
    [InlineData("EN", "hu", "en", "HU", 0)] // case doesn't matter
    [InlineData("en", "de", "en", "de-AT", 1)] // a regional variant of the TM's target
    [InlineData("en", "de", "en-US", "de-AT", 1)] // one penalty, however many sides differ
    [InlineData("en", "de-AT", "en", "de-AT", 0)]
    [InlineData("en", "de-AT", "en", "de", null)] // a regional TM serves only its own region
    [InlineData("en", "de-AT", "en", "de-DE", null)]
    [InlineData("en", "de", "en", "fr", null)]
    [InlineData("en", "de", "fr", "de", null)]
    [InlineData("en", "de", "en", "deu", null)] // a prefix that isn't a region
    [InlineData("en", "de", "en", null, 0)] // no target language yet: the source decides
    [InlineData("en", "de", "en-GB", null, 1)]
    [InlineData("en", "de", "fr", null, null)]
    public void Penalty_FollowsDecision3(string tmSource, string tmTarget, string documentSource, string? documentTarget, int? expected) =>
        Assert.Equal(expected, TmLanguages.Penalty(tmSource, tmTarget, documentSource, documentTarget));
}
