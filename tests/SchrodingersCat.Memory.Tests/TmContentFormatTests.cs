using CsCheck;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Memory.Tests;

public class TmContentFormatTests
{
    private static readonly Gen<InlineContent> Contents =
        XliffGen.Document.Select(d => d.Units.SelectMany(u => u.Segments).SelectMany(s => new[] { s.Source, s.Target }).OfType<InlineContent>().ToArray())
            .Where(c => c.Length > 0)
            .SelectMany(c => Gen.OneOfConst(c));

    [Fact]
    public void EveryContent_RoundTripsExactly() =>
        Contents.Sample(content => Assert.Equal(content, TmContentFormat.Read(TmContentFormat.Write(content))));

    [Fact]
    public void InvalidCodePoints_Survive()
    {
        var content = InlineContent.FromText("a\uD800b￿\uDC00");

        Assert.Equal(content, TmContentFormat.Read(TmContentFormat.Write(content)));
    }

    // A TM file can come from anyone: damaged content fails only with TmFormatException, or reads as some content.
    [Fact]
    public void DamagedContent_FailsOnlyWithTmFormatException() =>
        Gen.Select(Contents, Gen.Int[0, 1000].Array[1, 4], Gen.Byte.Array[1, 4]).Sample((content, positions, values) =>
        {
            var data = TmContentFormat.Write(content);
            foreach (var (position, value) in positions.Zip(values))
                data[position % data.Length] = value;
            var truncated = data[..(positions[0] % (data.Length + 1))];

            foreach (var damaged in new[] { data, truncated })
            {
                try
                {
                    TmContentFormat.Read(damaged);
                }
                catch (TmFormatException)
                {
                }
            }
        });

    [Fact]
    public void AnUnknownVersion_IsRefused() =>
        Assert.Throws<TmFormatException>(() => TmContentFormat.Read([2, 0]));

    [Fact]
    public void AHugeCount_IsRefusedBeforeAllocating() =>
        Assert.Throws<TmFormatException>(() => TmContentFormat.Read([1, 0xFF, 0xFF, 0xFF, 0xFF, 0x07]));
}
