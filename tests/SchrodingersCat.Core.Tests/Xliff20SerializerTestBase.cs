using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

/// <summary>Helpers for tests that read and write XLIFF through <see cref="Xliff20Serializer"/>.</summary>
public abstract class Xliff20SerializerTestBase
{
    protected const string FsNamespace = "urn:oasis:names:tc:xliff:fs:2.0";

    protected virtual Xliff20Serializer Serializer { get; } = new();

    /// <summary>Wraps <c>&lt;unit&gt;</c> elements in a minimal document with a single file.</summary>
    protected static string WrapUnits(string units) => $"""
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:fs="{FsNamespace}" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">{units}</file>
        </xliff>
        """;

    /// <summary>Wraps the content of a unit in a minimal document with a single unit <c>u1</c>.</summary>
    protected static string WrapUnitContent(string unitContent) => WrapUnits($"""<unit id="u1">{unitContent}</unit>""");

    /// <summary>A document with a single unit holding one segment with the given source.</summary>
    protected static TranslationDocument DocumentWith(params InlineElement[] source)
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Id = "s1", Source = new InlineContent(source) });
        var file = new TranslationFile { Id = "f1" };
        file.Items.Add(unit);
        var document = new TranslationDocument { SourceLanguage = "en" };
        document.Files.Add(file);
        return document;
    }

    protected async Task<TranslationDocument> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await Serializer.ReadAsync(stream);
    }

    protected async Task<TranslationUnit> ReadUnitAsync(string unitContent) =>
        (await ReadAsync(WrapUnitContent(unitContent))).Units.Single();

    protected async Task<string> WriteAsync(TranslationDocument document)
    {
        using var stream = new MemoryStream();
        await Serializer.WriteAsync(document, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
