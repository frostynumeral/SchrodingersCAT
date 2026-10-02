using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

public class Xliff20SerializerLoggingTests
{
    private const string Sample = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en-US">
          <file id="f1">
            <unit id="u1"><segment><source>Hello</source></segment></unit>
          </file>
        </xliff>
        """;

    private readonly FakeLogger<Xliff20Serializer> _logger = new();

    private Xliff20Serializer Serializer => new(_logger);

    private static MemoryStream ToStream(string xml) => new(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public async Task Read_LogsSummaryAtDebug()
    {
        await Serializer.ReadAsync(ToStream(Sample));

        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Debug, record.Level);
        Assert.Equal("Read XLIFF document with 1 file(s) and 1 unit(s)", record.Message);
    }

    [Fact]
    public async Task Read_InvalidInput_LogsErrorAndRethrows()
    {
        var thrown = await Assert.ThrowsAsync<XliffFormatException>(
            () => Serializer.ReadAsync(ToStream("<notxliff/>")));

        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Same(thrown, record.Exception);
    }

    [Fact]
    public async Task Write_InvalidModel_LogsErrorAndRethrows()
    {
        var unit = new TranslationUnit { Id = "u1" };
        // An opening code with no closing half is something the reader would reject.
        unit.Segments.Add(new Segment { Id = "s1", Source = new InlineContent([new InlineCode("1", InlineCodeKind.Opening)]) });
        var file = new TranslationFile { Id = "f1" };
        file.Units.Add(unit);
        var document = new TranslationDocument { SourceLanguage = "en-US" };
        document.Files.Add(file);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Serializer.WriteAsync(document, new MemoryStream()));

        var record = _logger.LatestRecord;
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Same(thrown, record.Exception);
    }

    [Fact]
    public async Task WithoutLogger_StillWorks()
    {
        var document = await new Xliff20Serializer().ReadAsync(ToStream(Sample));

        Assert.Single(document.Files);
    }
}
