using System.Text;
using System.Xml.Linq;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Core.Tests.Properties;

/// <summary>
/// Breaks a valid XLIFF file in random ways the reader has to cope with: structural edits to the XML tree,
/// edits to the raw text, and byte flips that can leave invalid UTF-8.
/// </summary>
internal static class XmlMutator
{
    private static readonly XNamespace Xliff = XliffGen.Xliff;

    private static readonly XName[] ElementNames =
    [
        .. new[]
        {
            "xliff", "file", "skeleton", "group", "unit", "notes", "note", "originalData", "data", "segment", "ignorable",
            "source", "target", "ph", "pc", "sc", "ec", "mrk", "sm", "em", "cp", "bogus",
        }.Select(n => Xliff + n),
        XName.Get("ext", "urn:example:tool"),
        XName.Get("plain"),
    ];

    private static readonly XName[] AttributeNames =
    [
        "id", "startRef", "dataRef", "dataRefStart", "state", "subState", "order", "priority", "translate", "canOverlap",
        "canReorder", "isolated", "dir", "srcDir", "href", "hex", "version", "srcLang", "trgLang", "appliesTo", "foo",
        XNamespace.Xml + "space", XNamespace.Xml + "lang", XName.Get("fs", "urn:oasis:names:tc:xliff:fs:2.0"),
    ];

    private static readonly string[] AttributeValues =
    [
        "", "yes", "no", "maybe", "0", "-1", "1", "11", "FFFE", "110000", "zz", "firstNo", "ltr", "up", "preserve",
        "c1", "m1", "s1", "u1", "g1", "d0", "n1", "f1", "translated", "final", "bogus", "1.2", "2.1",
    ];

    private static readonly string[] Snippets =
    [
        "<", ">", "&", "\"", "\u0001", "<x>", "</source>", "&#1;", "&#xD800;", "&bogus;", "]]>", "<![CDATA[x]]>", "<!-- c -->", "<?pi x?>",
        "&e;", "&xxe;", "&l3;", "%p;",
    ];

    private static readonly string[] EntityReferences = ["&e;", "&xxe;", "&l3;", ""];

    // Inserted where a DOCTYPE is allowed (after the XML declaration), with entity references in the content.
    private static readonly string[] Doctypes =
    [
        "<!DOCTYPE xliff>",
        "<!DOCTYPE xliff [<!ENTITY e \"x\">]>",
        "<!DOCTYPE xliff [<!ENTITY l0 \"lol\"><!ENTITY l1 \"&l0;&l0;&l0;&l0;\"><!ENTITY l2 \"&l1;&l1;&l1;&l1;\"><!ENTITY l3 \"&l2;&l2;&l2;&l2;\">]>",
        "<!DOCTYPE xliff [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>",
        "<!DOCTYPE xliff SYSTEM \"http://example.invalid/xliff.dtd\">",
        "<!DOCTYPE xliff [<!ENTITY % p \"<!ENTITY e 'x'>\"> %p;]>",
    ];

    public static byte[] Mutate(string xml, Choices choices)
    {
        var count = 1 + choices.Next(3);
        for (var i = 0; i < count; i++)
        {
            xml = choices.Next(5) switch
            {
                0 => MutateText(xml, choices),
                1 => AddDoctype(xml, choices),
                _ => MutateTree(xml, choices),
            };
        }

        var bytes = Encoding.UTF8.GetBytes(xml);
        if (choices.Next(6) == 0 && bytes.Length > 0)
            bytes[choices.Next(bytes.Length)] = (byte)choices.Next(256);
        return bytes;
    }

    private static string AddDoctype(string xml, Choices choices)
    {
        var declarationEnd = xml.StartsWith("<?xml", StringComparison.Ordinal) ? xml.IndexOf("?>", StringComparison.Ordinal) + 2 : 0;
        xml = xml.Insert(declarationEnd, choices.Pick(Doctypes));

        // Use an entity the DTD may have declared, where content is allowed.
        var source = xml.IndexOf("<source>", StringComparison.Ordinal);
        return source < 0 ? xml : xml.Insert(source + "<source>".Length, choices.Pick(EntityReferences));
    }

    private static string MutateText(string xml, Choices choices)
    {
        var at = choices.Next(xml.Length + 1);
        return choices.Next(3) switch
        {
            0 when xml.Length > 0 => xml.Remove(Math.Min(at, xml.Length - 1), 1),
            1 => xml[..at],
            _ => xml.Insert(at, choices.Pick(Snippets)),
        };
    }

    private static string MutateTree(string xml, Choices choices)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return xml; // an earlier text edit broke it; leave that for the reader to reject
        }

        var elements = document.Root!.DescendantsAndSelf().ToList();
        var element = choices.Pick(elements);
        switch (choices.Next(9))
        {
            case 0 when element.Parent is not null:
                element.Remove();
                break;
            case 1 when element.Parent is not null:
                element.AddAfterSelf(new XElement(element));
                break;
            case 2:
                element.Name = choices.Pick(ElementNames);
                break;
            case 3 when element.Attributes().Any():
                choices.Pick(element.Attributes().ToList()).Remove();
                break;
            case 4 when element.Attributes().Any(a => !a.IsNamespaceDeclaration):
                choices.Pick(element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList()).Value = choices.Pick(AttributeValues);
                break;
            case 5:
                element.SetAttributeValue(choices.Pick(AttributeNames), choices.Pick(AttributeValues));
                break;
            case 6:
                element.Add(new XElement(choices.Pick(ElementNames), new XAttribute("id", choices.Pick(AttributeValues))));
                break;
            case 7:
                element.AddFirst(choices.Next(2) == 0 ? " " : "x");
                break;
            default:
                var target = choices.Pick(elements);
                if (element.Parent is not null && !target.AncestorsAndSelf().Contains(element))
                {
                    element.Remove();
                    target.Add(element);
                }

                break;
        }

        try
        {
            return document.ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
        {
            // XmlWriter refuses some trees (an invalid xml:space, a clashing default namespace); break the text instead.
            return MutateText(xml, choices);
        }
    }
}
