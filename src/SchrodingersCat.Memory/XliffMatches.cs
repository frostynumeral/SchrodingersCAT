using System.Globalization;
using System.Xml.Linq;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Memory;

/// <summary>A match the XLIFF file brought along (matches module), and the segment it's for.</summary>
/// <param name="SegmentId">The segment the match's <c>ref</c> points to; null if it points elsewhere (e.g. an annotation).</param>
public sealed record XliffMatch(string? SegmentId, TmMatch Match);

/// <summary>
/// Reads the matches a client's file carries in a unit (<c>&lt;mtc:matches&gt;</c>, kept verbatim by Core in
/// <see cref="TranslationItem.ExtensionElements"/>), as matches beside the TM's (design section 5). They are only
/// read: Core writes the elements back as they were.
/// </summary>
/// <remarks>
/// The score is <c>matchQuality</c>, else <c>similarity</c>, rounded (0 if neither is there). Content is read with
/// the XLIFF reader's own rules and the match's <c>&lt;originalData&gt;</c>. A match that breaks them is left out
/// rather than failing the unit: they are someone else's suggestions, not the translation.
/// </remarks>
public static class XliffMatches
{
    public static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:matches:2.0";

    public static IReadOnlyList<XliffMatch> Read(TranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var matches = new List<XliffMatch>();
        foreach (var match in unit.ExtensionElements.Where(e => e.Name == Ns + "matches").SelectMany(e => e.Elements(Ns + "match")))
        {
            if (TryRead(unit, match) is { } read)
                matches.Add(read);
        }

        return matches;
    }

    private static XliffMatch? TryRead(TranslationUnit unit, XElement match)
    {
        var core = Xliff20Serializer.Ns;
        if (match.Element(core + "source") is not { } sourceElement || match.Element(core + "target") is not { } targetElement)
            return null;

        try
        {
            var originalData = OriginalData(match.Element(core + "originalData"));
            var context = $"{unit.Id} (match)";
            var entry = new TmEntry(
                Xliff20Serializer.ReadStandaloneContent(sourceElement, originalData, context),
                Xliff20Serializer.ReadStandaloneContent(targetElement, originalData, context));

            var reference = (string?)match.Attribute("ref");
            var segmentId = reference is ['#', .. var id] && unit.Segments.Any(s => s.Id == id) ? id : null;
            var origin = (string?)match.Attribute("origin");
            return new XliffMatch(segmentId, new TmMatch(entry, Score(match)) { Origin = origin });
        }
        catch (XliffFormatException)
        {
            return null;
        }
    }

    private static int Score(XElement match)
    {
        foreach (var name in new[] { "matchQuality", "similarity" })
        {
            if (double.TryParse((string?)match.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100)
                return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        return 0;
    }

    // <data> values, with <cp> as the character it stands for.
    private static Dictionary<string, string> OriginalData(XElement? element)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in element?.Elements(Xliff20Serializer.Ns + "data") ?? [])
        {
            if ((string?)item.Attribute("id") is not { } id)
                throw new XliffFormatException("<data> in a match has no id.");

            data[id] = string.Concat(item.Nodes().Select(n => n switch
            {
                XText text => text.Value,
                XElement { Name.LocalName: "cp" } cp when int.TryParse((string?)cp.Attribute("hex"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                    && code is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) => char.ConvertFromUtf32(code),
                _ => throw new XliffFormatException("<data> in a match has content that isn't text or <cp>."),
            }));
        }

        return data;
    }
}
