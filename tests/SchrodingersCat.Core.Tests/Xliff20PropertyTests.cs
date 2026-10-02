using CsCheck;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Tests.Properties;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// Property-based tests over generated documents. A failure prints the shrunk example and a seed; replay it with
/// <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests.&lt;test&gt;"</c>.
/// Run longer with <c>CsCheck_Iter=100000</c> or <c>CsCheck_Time=60</c> (seconds).
/// </summary>
public class Xliff20PropertyTests : Xliff20SerializerTestBase
{
    private static readonly Gen<(DocumentPlan Plan, int[] Choices)> Mutation = Gen.Select(XliffGen.DocumentPlan, Gen.Int.Array[1, 12]);

    [Fact]
    public Task WriteThenRead_ValidDocuments_IsLossless() =>
        XliffGen.Document.SampleAsync(async document =>
        {
            var xml = await WriteAsync(document);
            var reread = await ReadAsync(xml);

            if (ModelComparer.FindDifference(document, reread) is { } difference)
                Assert.Fail($"The model changed in a write→read round-trip at {difference}\n{xml}");
        }, print: PropertyPrint.Document);

    [Fact]
    public Task WriteReadWrite_ValidDocuments_IsStable() =>
        XliffGen.Document.SampleAsync(async document =>
        {
            var xml = await WriteAsync(document);

            Assert.Equal(xml, await WriteAsync(await ReadAsync(xml)));
        }, print: PropertyPrint.Document);

    /// <summary>
    /// The writer either refuses a broken model with one of its documented exceptions, or writes a file the reader
    /// accepts and that writes back identically. Anything else (another exception, a file the reader rejects) fails.
    /// </summary>
    [Fact]
    public Task Write_MutatedDocuments_FailOnlyWithDocumentedExceptions() =>
        Mutation.SampleAsync(async sample =>
        {
            var document = DocumentBuilder.Build(sample.Plan);
            ModelMutator.Mutate(document, new Choices(sample.Choices));

            string xml;
            try
            {
                xml = await WriteAsync(document);
            }
            catch (Exception ex) when (ex.GetType() == typeof(InvalidOperationException) || ex.GetType() == typeof(NotSupportedException))
            {
                return;
            }

            var reread = await ReadAsync(xml);
            Assert.Equal(xml, await WriteAsync(reread));
        }, print: PropertyPrint.MutatedDocument);

    /// <summary>
    /// The reader either rejects a broken file with <see cref="XliffFormatException"/>, or returns a model the writer
    /// can write, and that writes back identically. Anything else fails.
    /// </summary>
    [Fact]
    public Task Read_MutatedXml_FailsOnlyWithXliffFormatException() =>
        Mutation.SampleAsync(async sample =>
        {
            var xml = await WriteAsync(DocumentBuilder.Build(sample.Plan));
            var bytes = XmlMutator.Mutate(xml, new Choices(sample.Choices));

            TranslationDocument document;
            try
            {
                using var stream = new MemoryStream(bytes);
                document = await Serializer.ReadAsync(stream);
            }
            catch (XliffFormatException)
            {
                return;
            }

            var written = await WriteAsync(document);
            Assert.Equal(written, await WriteAsync(await ReadAsync(written)));
        }, print: PropertyPrint.MutatedXml);

    /// <summary>
    /// The properties are only as good as the generators: check that a sample really contains what they claim to
    /// generate, and that mutations are sometimes accepted and sometimes rejected (so neither branch is untested).
    /// </summary>
    [Fact]
    public async Task Generators_CoverWhatThePropertiesClaim()
    {
        var documents = XliffGen.Document.Array[300].Single();
        var written = new List<string>();
        foreach (var document in documents)
            written.Add(await WriteAsync(document));

        var features = new Dictionary<string, Func<TranslationDocument, string, bool>>
        {
            ["nested group"] = (d, _) => d.Files.SelectMany(f => f.Items).OfType<TranslationGroup>().Any(g => g.Items.OfType<TranslationGroup>().Any()),
            ["pair spanning segments"] = (d, _) => d.Units.Any(SpansSegments),
            ["<pc>"] = (_, xml) => xml.Contains("<pc ", StringComparison.Ordinal),
            ["<sc>/<ec>"] = (_, xml) => xml.Contains("<ec ", StringComparison.Ordinal),
            ["<mrk>"] = (_, xml) => xml.Contains("<mrk ", StringComparison.Ordinal),
            ["<sm>/<em>"] = (_, xml) => xml.Contains("<em ", StringComparison.Ordinal),
            ["isolated code"] = (_, xml) => xml.Contains("isolated=\"yes\"", StringComparison.Ordinal),
            ["<cp>"] = (_, xml) => xml.Contains("<cp ", StringComparison.Ordinal),
            ["escaped carriage return"] = (_, xml) => xml.Contains("&#xD;", StringComparison.Ordinal),
            ["xml:space=preserve"] = (_, xml) => xml.Contains("xml:space=\"preserve\"", StringComparison.Ordinal),
            ["ignorable"] = (d, _) => d.Units.Any(u => u.Segments.Any(s => s.IsIgnorable)),
            ["target order"] = (d, _) => d.Units.Any(u => u.Segments.Any(s => s.TargetOrder is not null)),
            ["note"] = (d, _) => d.Units.Any(u => u.Notes.Count > 0) && d.Files.Any(f => f.Notes.Count > 0),
            ["extension element"] = (d, _) => d.Units.Any(u => u.ExtensionElements.Count > 0),
            ["extension attribute on a code"] = (d, _) => d.Units.SelectMany(u => u.Segments).SelectMany(s => s.Source).OfType<InlineCode>().Any(c => c.ExtensionAttributes.Count > 0),
            ["skeleton"] = (d, _) => d.Files.Any(f => f.Skeleton is not null),
            ["several files"] = (d, _) => d.Files.Count > 1,
        };

        var missing = features.Where(f => !documents.Zip(written).Any(p => f.Value(p.First, p.Second))).Select(f => f.Key).ToList();
        Assert.True(missing.Count == 0, $"No generated document has: {string.Join(", ", missing)}");

        var mutations = Mutation.Array[300].Single();
        var (writerAccepted, writerRejected, readerAccepted, readerRejected, doctypesRejected) = (0, 0, 0, 0, 0);
        foreach (var (plan, choices) in mutations)
        {
            var document = DocumentBuilder.Build(plan);
            ModelMutator.Mutate(document, new Choices(choices));
            try
            {
                await WriteAsync(document);
                writerAccepted++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                writerRejected++;
            }

            var xml = await WriteAsync(DocumentBuilder.Build(plan));
            var mutated = XmlMutator.Mutate(xml, new Choices(choices));
            try
            {
                using var stream = new MemoryStream(mutated);
                await Serializer.ReadAsync(stream);
                readerAccepted++;
            }
            catch (XliffFormatException ex)
            {
                readerRejected++;
                if (ex.Message.Contains("DTD", StringComparison.Ordinal))
                    doctypesRejected++;
            }
        }

        Assert.All([writerAccepted, writerRejected, readerAccepted, readerRejected, doctypesRejected], count => Assert.True(count > 0,
            $"writer accepted {writerAccepted}, rejected {writerRejected}; reader accepted {readerAccepted}, rejected {readerRejected} ({doctypesRejected} for a DTD)"));
    }

    private static bool SpansSegments(TranslationUnit unit)
    {
        var opened = new Dictionary<string, int>();
        for (var i = 0; i < unit.Segments.Count; i++)
        {
            foreach (var code in unit.Segments[i].Source.OfType<InlineCode>().Where(c => !c.IsIsolated))
            {
                if (code.Kind == InlineCodeKind.Opening)
                    opened[code.Id] = i;
                else if (code.Kind == InlineCodeKind.Closing && opened.TryGetValue(code.Id, out var start) && start != i)
                    return true;
            }
        }

        return false;
    }
}
