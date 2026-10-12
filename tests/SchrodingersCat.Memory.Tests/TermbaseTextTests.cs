using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public class TermbaseTextTests
{
    [Theory]
    [InlineData("")]
    [InlineData("fájl")]
    [InlineData("\uD800 \uDC00 \0 ￿ ￼")]
    [InlineData("\U0001D400")]
    public void Strings_RoundTripExactly(string text) => Assert.Equal(text, TermbaseText.Decode(TermbaseText.Write(text)));

    [Fact]
    public void TheBytes_AreLittleEndianUtf16() => Assert.Equal([0x61, 0x00, 0x00, 0xD8], TermbaseText.Write("a\uD800"));

    [Fact]
    public void AnOddLength_OrAnotherType_IsDamage()
    {
        Assert.Throws<TermbaseFormatException>(() => TermbaseText.Decode(new byte[] { 0x61 }));
        Assert.Throws<TermbaseFormatException>(() => TermbaseText.Decode("text"));
        Assert.Throws<TermbaseFormatException>(() => TermbaseText.Decode(5L));
    }
}
