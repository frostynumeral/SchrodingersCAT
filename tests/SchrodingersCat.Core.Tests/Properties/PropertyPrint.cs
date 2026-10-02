using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests.Properties;

/// <summary>How CsCheck shows a failing (shrunk) example: as the XLIFF it produces, which is what you'd debug.</summary>
internal static class PropertyPrint
{
    private static readonly Xliff20Serializer Serializer = new();

    public static string Document(TranslationDocument document) => TryWrite(document);

    public static string MutatedDocument((DocumentPlan Plan, int[] Choices) sample)
    {
        var document = DocumentBuilder.Build(sample.Plan);
        var before = TryWrite(document);
        ModelMutator.Mutate(document, new Choices(sample.Choices));
        return $"Before the mutation:\n{before}\nAfter:\n{TryWrite(document)}";
    }

    public static string MutatedXml((DocumentPlan Plan, int[] Choices) sample)
    {
        var xml = TryWrite(DocumentBuilder.Build(sample.Plan));
        return $"Mutated XML:\n{Encoding.UTF8.GetString(XmlMutator.Mutate(xml, new Choices(sample.Choices)))}";
    }

    // The printer runs outside the test body, after CsCheck has found the failure, so blocking here is harmless.
    private static string TryWrite(TranslationDocument document)
    {
        try
        {
            using var stream = new MemoryStream();
            Serializer.WriteAsync(document, stream).GetAwaiter().GetResult();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or NullReferenceException)
        {
            return $"(the writer threw {ex.GetType().Name}: {ex.Message})";
        }
    }
}
