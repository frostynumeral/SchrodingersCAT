using System.Diagnostics;
using System.Globalization;
using System.Text;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// XLIFF files come from clients and vendors, so the reader treats them as untrusted: no DTDs (and so no entity
/// expansion or external entities), bounded size, bounded nesting. Each attack fails with
/// <see cref="XliffFormatException"/>, quickly, instead of exhausting memory or crashing the process.
/// </summary>
public class Xliff20UntrustedInputTests : Xliff20SerializerTestBase
{
    private const string Head = """<xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en"><file id="f1"><unit id="u1"><segment><source>""";
    private const string Tail = "</source></segment></unit></file></xliff>";

    [Fact]
    public async Task Read_RejectsBillionLaughs()
    {
        var dtd = new StringBuilder("""<!DOCTYPE xliff [<!ENTITY l0 "lol">""");
        for (var i = 1; i <= 9; i++)
            dtd.Append(CultureInfo.InvariantCulture, $"""<!ENTITY l{i} "{string.Concat(Enumerable.Repeat($"&l{i - 1};", 10))}">""");
        dtd.Append("]>");
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(dtd + Head + "&l9;" + Tail));

        Assert.Contains("DTD", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}; the DTD should be refused before any expansion.");
    }

    [Fact]
    public async Task Read_RejectsExternalEntities()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(secret, "TOP-SECRET");
        try
        {
            var xml = $"""<!DOCTYPE xliff [<!ENTITY xxe SYSTEM "{new Uri(secret)}">]>""" + Head + "&xxe;" + Tail;

            var ex = await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(xml));

            Assert.Contains("DTD", ex.Message);
            Assert.DoesNotContain("TOP-SECRET", ex.ToString());
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Theory]
    [InlineData("""<!DOCTYPE xliff>""")]
    [InlineData("""<!DOCTYPE xliff SYSTEM "http://example.invalid/xliff.dtd">""")]
    [InlineData("""<!DOCTYPE xliff [<!ENTITY e "x">]>""")]
    [InlineData("""<!DOCTYPE xliff [<!ENTITY % p "<!ENTITY e 'x'>"> %p;]>""")]
    public async Task Read_RejectsAnyDtd(string doctype)
    {
        var ex = await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(doctype + Head + "x" + Tail));

        Assert.Contains("DTD", ex.Message);
    }

    [Fact]
    public async Task Read_StillAcceptsPredefinedEntitiesAndCharacterReferences()
    {
        var unit = (await ReadAsync(Head + "&lt;&amp;&gt;&quot;&apos;&#x41;&#66;" + Tail)).Units.Single();

        Assert.Equal("<&>\"'AB", unit.Segments[0].Source.ToPlainText());
    }

    [Fact]
    public async Task Read_RejectsInputOverTheSizeLimit()
    {
        var serializer = new Xliff20Serializer { MaxInputBytes = 1_000 };
        var xml = Head + new string('a', 2_000) + Tail;

        var ex = await Assert.ThrowsAsync<XliffFormatException>(() => serializer.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml))));

        Assert.Contains("1,000-byte limit", ex.Message);
    }

    [Fact]
    public async Task Read_AcceptsInputExactlyAtTheSizeLimit()
    {
        var bytes = Encoding.UTF8.GetBytes(Head + "a" + Tail);
        var serializer = new Xliff20Serializer { MaxInputBytes = bytes.Length };

        var document = await serializer.ReadAsync(new MemoryStream(bytes));

        Assert.Equal("a", document.Units.Single().Segments[0].Source.ToPlainText());
    }

    [Fact]
    public async Task Read_EndlessInputStopsAtTheSizeLimit()
    {
        var serializer = new Xliff20Serializer { MaxInputBytes = 1_000_000 };
        using var endless = new EndlessStream(Encoding.UTF8.GetBytes(Head), (byte)'a');

        await Assert.ThrowsAsync<XliffFormatException>(() => serializer.ReadAsync(endless));

        Assert.InRange(endless.BytesRead, 1_000_000, 1_100_000);
    }

    [Theory]
    [InlineData("pc", 10_000)]
    [InlineData("mrk", 100_000)]
    public async Task Read_RejectsDeeplyNestedInlineCodesWithoutCrashing(string element, int depth)
    {
        var open = string.Concat(Enumerable.Range(0, depth).Select(i => $"""<{element} id="{i}">"""));
        var close = string.Concat(Enumerable.Repeat($"</{element}>", depth));
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(Head + open + close + Tail));

        Assert.Contains($"more than {Xliff20Serializer.MaxElementDepth} deep", ex.Message);
        // Building a deep XML tree is quadratic, so the limit must stop the parser, not check the finished tree.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Read_RejectsDeeplyNestedGroupsWithoutCrashing()
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(NestedGroups(10_000)));
    }

    // <xliff>, <file>, the groups, <unit>, <segment>, <source>: the deepest element is at groups + 5.
    [Theory]
    [InlineData(Xliff20Serializer.MaxElementDepth - 5, true)]
    [InlineData(Xliff20Serializer.MaxElementDepth - 4, false)]
    public async Task Read_NestingLimitIsExact(int groups, bool accepted)
    {
        var read = ReadAsync(NestedGroups(groups));

        if (accepted)
            Assert.Single((await read).Units);
        else
            await Assert.ThrowsAsync<XliffFormatException>(() => read);
    }

    private static string NestedGroups(int depth) =>
        """<xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en"><file id="f1">"""
        + string.Concat(Enumerable.Range(0, depth).Select(i => $"""<group id="g{i}">"""))
        + """<unit id="u1"><segment><source>x</source></segment></unit>"""
        + string.Concat(Enumerable.Repeat("</group>", depth))
        + "</file></xliff>";

    /// <summary>A prefix followed by the same byte forever, counting how much was read.</summary>
    private sealed class EndlessStream(byte[] prefix, byte filler) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
                buffer[offset + i] = BytesRead + i < prefix.Length ? prefix[BytesRead + i] : filler;
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
