using System.Text;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace EditorSpike;

/// <summary>The segment the spike edits: a pair, a protected placeholder and an annotation, plus some RTL text to try.</summary>
public static class SampleUnit
{
    public const string Xliff = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1">
              <originalData>
                <data id="d1">&lt;b&gt;</data>
                <data id="d2">&lt;/b&gt;</data>
                <data id="d3">{0}</data>
              </originalData>
              <segment id="s1">
                <source>Press <pc id="1" dataRefStart="d1" dataRefEnd="d2" subType="xlf:b">OK</pc> to copy <ph id="2" dataRef="d3" disp="{0}" canDelete="no" canCopy="no"/> <mrk id="m1" type="term">files</mrk>.</source>
                <target>Nyomd meg az <pc id="1" dataRefStart="d1" dataRefEnd="d2" subType="xlf:b">OK</pc> gombot <ph id="2" dataRef="d3" disp="{0}" canDelete="no" canCopy="no"/> <mrk id="m1" type="term">fájl</mrk> másolásához.</target>
              </segment>
            </unit>
          </file>
        </xliff>
        """;

    /// <summary>The same segment translated into Hebrew, for the right-to-left checks (<c>--rtl</c>).</summary>
    public static string RightToLeftXliff => Xliff
        .Replace("trgLang=\"hu\"", "trgLang=\"he\"", StringComparison.Ordinal)
        .Replace("Nyomd meg az ", "לחץ על ", StringComparison.Ordinal)
        .Replace(" gombot ", " כדי להעתיק ", StringComparison.Ordinal)
        .Replace("fájl", "קבצים", StringComparison.Ordinal)
        .Replace(" másolásához.", ".", StringComparison.Ordinal);

    public static TranslationUnit Load(bool rightToLeft = false)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rightToLeft ? RightToLeftXliff : Xliff));
        return new Xliff20Serializer().ReadAsync(stream).GetAwaiter().GetResult().Units.Single();
    }
}
