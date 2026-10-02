using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

public class Xliff20SerializerLoggingTests : Xliff20SerializerTestBase
{
    private const string Sample = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en-US">
          <file id="f1">
            <unit id="u1"><segment><source>Hello</source></segment></unit>
          </file>
        </xliff>
        """;

    private readonly FakeLogger<Xliff20Serializer> _logger = new();

    protected override Xliff20Serializer Serializer => new(_logger);

    [Fact]
    public async Task Read_LogsSummaryAtDebug()
    {
        await ReadAsync(Sample);

        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Debug, record.Level);
        Assert.Equal("Read XLIFF document with 1 file(s) and 1 unit(s)", record.Message);
    }

    [Fact]
    public async Task Read_InvalidInput_LogsErrorAndRethrows()
    {
        var thrown = await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync("<notxliff/>"));

        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Same(thrown, record.Exception);
    }

    [Fact]
    public async Task Write_InvalidModel_LogsErrorAndRethrows()
    {
        // An opening code with no closing half is something the reader would reject.
        var document = DocumentWith(new InlineCode("1", InlineCodeKind.Opening));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(document));

        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Same(thrown, record.Exception);
    }

    [Fact]
    public async Task WithoutLogger_StillWorks()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Sample));
        var document = await new Xliff20Serializer().ReadAsync(stream);

        Assert.Single(document.Files);
    }
}
