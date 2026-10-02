using System.Xml.Linq;
using CsCheck;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.TestSupport;

/// <summary>
/// CsCheck generators for valid <see cref="TranslationDocument"/>s: models the reader could have produced.
/// </summary>
/// <remarks>
/// The generators produce a plan (tokens for text, codes, pair halves, markers; groups, notes, extensions), and
/// <see cref="DocumentBuilder"/> turns it into a model, assigning ids and matching or closing pairs. Keeping the
/// randomness in the plan lets CsCheck shrink a failing document to a small one.
/// </remarks>
public static class XliffGen
{
    public const string Xliff = "urn:oasis:names:tc:xliff:document:2.0";

    // Valid in XML attributes and text, including whitespace that XML parsers normalize unless it is escaped.
    private static readonly string[] SafePieces =
    [
        "a", "Hello", "é", "日本語", "\U0001F600", " ", "  ", "\t", "\n", "\r", "\r\n",
        "<", "&", "\"", "'", ">", "]]>", "x:y", "\u00A0", "\u2028", "\u0085",
    ];

    // Only content (<source>, <target>, <data>) can carry these, as <cp>.
    private static readonly string[] ContentOnlyPieces = ["\u0001", "\u001B", "\uFFFE", "\uFFFF", "\uD800", "\uDFFF"];

    public static readonly string[] AttributeNamespaces =
        ["urn:oasis:names:tc:xliff:fs:2.0", "urn:example:tool", "http://www.w3.org/2005/11/its", "urn:example:other"];

    public static readonly string[] AttributeNames = ["fs", "a", "b", "flag", "hash"];

    private static readonly XNamespace[] ExtensionNamespaces =
        ["urn:example:tool", "urn:oasis:names:tc:xliff:matches:2.0", "urn:example:other"];

    private static readonly XNamespace[] NestedNamespaces = [.. ExtensionNamespaces, XNamespace.None, Xliff];

    private static readonly string[] ElementNames = ["data", "match", "entry", "x"];

    private static readonly string?[] Directions = [null, "ltr", "rtl", "auto"];

    private static readonly string[] Languages = ["en", "en-US", "hu", "de-CH", "zh-Hant-TW"];

    public static readonly Gen<string> SafeText = Gen.OneOfConst(SafePieces).Array[0, 4].Select(string.Concat);

    public static readonly Gen<string> ContentText =
        Gen.OneOfConst([.. SafePieces, .. ContentOnlyPieces]).Array[1, 4].Select(string.Concat);

    // XML has no escape for a carriage return in a comment, and no comment may contain "--" or end with "-".
    private static readonly Gen<string> CommentText =
        Gen.OneOfConst(SafePieces.Where(p => !p.Contains('\r', StringComparison.Ordinal)).ToArray()).Array[0, 3].Select(string.Concat);

    private static readonly Gen<string?> OptionalText = Gen.Null(SafeText, 0.6);

    private static readonly Gen<bool?> OptionalBool = Gen.OneOfConst<bool?>(null, null, true, false);

    private static readonly Gen<string?> Direction = Gen.OneOfConst(Directions);

    public static readonly Gen<ExtensionAttrPlan[]> ExtensionAttributes =
        Gen.Select(Gen.Int[0, AttributeNamespaces.Length - 1], Gen.Int[0, AttributeNames.Length - 1], SafeText,
                (ns, name, value) => new ExtensionAttrPlan(ns, name, value))
            .Array[0, 2];

    public static Gen<XElement> Element(int depth) => Element(ExtensionNamespaces, depth);

    private static Gen<XElement> Element(XNamespace[] namespaces, int depth) =>
        from ns in Gen.OneOfConst(namespaces)
        from name in Gen.OneOfConst(ElementNames)
        from attributes in Gen.Select(Gen.OneOfConst("a", "ref", "type"), SafeText).Array[0, 2]
        from children in Child(depth).Array[0, depth == 0 ? 1 : 3]
        select Build(ns + name, attributes, children);

    private static Gen<object> Child(int depth) =>
        depth == 0
            ? Gen.Frequency((3, SafeText.Select(t => (object)t)), (1, CommentText.Select(c => (object)new XComment(c))))
            : Gen.Frequency(
                (3, SafeText.Select(t => (object)t)),
                (2, Element(NestedNamespaces, depth - 1).Select(e => (object)e)),
                (1, CommentText.Select(c => (object)new XComment(c))));

    private static XElement Build(XName name, (string Name, string Value)[] attributes, object[] children)
    {
        var element = new XElement(name);
        foreach (var (attributeName, value) in attributes.DistinctBy(a => a.Name))
            element.Add(new XAttribute(attributeName, value));
        foreach (var child in children)
        {
            // Adjacent strings merge into one text node, as they would when the XML is read back.
            if (child is not "")
                element.Add(child);
        }

        return element;
    }

    private static readonly Gen<XElement?> Skeleton = Gen.Frequency(
        (6, Gen.Const((XElement?)null)),
        (1, SafeText.Select(href => (XElement?)new XElement(XName.Get("skeleton", Xliff), new XAttribute("href", href)))),
        (1, Element(1).Array[1, 2].Select(content => (XElement?)new XElement(XName.Get("skeleton", Xliff), content))));

    private static readonly Gen<NotePlan> Note =
        from hasId in Gen.Bool
        from text in SafeText
        from category in OptionalText
        from priority in Gen.Nullable(Gen.Int[1, 10], 0.6)
        from appliesTo in Gen.OneOfConst<NoteAppliesTo?>(null, NoteAppliesTo.Source, NoteAppliesTo.Target)
        from attributes in ExtensionAttributes
        select new NotePlan(hasId, text, category, priority, appliesTo, attributes);

    private static readonly Gen<XElement[]> Extensions = Gen.Frequency(
        (3, Gen.Const(Array.Empty<XElement>())),
        (1, Element(2).Array[1, 2]));

    private static readonly Gen<CodeAttrs> Code =
        from type in Gen.OneOfConst<string?>(null, null, "fmt", "ui", "other")
        from subType in Gen.OneOfConst<string?>(null, null, "xlf:b", "x:y")
        from copyOf in Gen.OneOfConst<string?>(null, null, null, "c1")
        from canCopy in Gen.Bool
        from canDelete in Gen.Bool
        from canReorder in Gen.Enum<CanReorder>()
        from canOverlap in Gen.Bool
        from dir in Direction
        from dataRef in Gen.Nullable(Gen.Int[0, 5], 0.5)
        from disp in OptionalText
        from equiv in OptionalText
        from subFlows in Gen.OneOfConst<string?>(null, null, null, "u1 u2")
        from attributes in Gen.Frequency((3, Gen.Const(Array.Empty<ExtensionAttrPlan>())), (1, ExtensionAttributes))
        select new CodeAttrs(type, subType, copyOf, canCopy, canDelete, canReorder, canOverlap, dir, dataRef, disp, equiv, subFlows, attributes);

    private static readonly Gen<MarkerAttrs> Marker =
        from translate in OptionalBool
        from type in Gen.OneOfConst<string?>(null, "term", "comment", "x:y")
        from reference in OptionalText
        from value in OptionalText
        from attributes in Gen.Frequency((3, Gen.Const(Array.Empty<ExtensionAttrPlan>())), (1, ExtensionAttributes))
        select new MarkerAttrs(translate, type, reference, value, attributes);

    private static readonly Gen<Token> Token = Gen.Frequency<Token>(
        (5, ContentText.Select(t => (Token)new TextToken(t))),
        (2, Code.Select(c => (Token)new StandaloneToken(c))),
        (2, Code.Select(c => (Token)new OpenToken(c))),
        (2, Gen.Select(Code, Gen.Int[0, 7], Gen.Int[0, 3], (c, pick, match) => (Token)new CloseToken(c, pick, match > 0))),
        (1, Code.Select(c => (Token)new IsolatedToken(c, IsOpening: true))),
        (1, Code.Select(c => (Token)new IsolatedToken(c, IsOpening: false))),
        (1, Marker.Select(m => (Token)new MarkerOpenToken(m))),
        (1, Gen.Int[0, 7].Select(pick => (Token)new MarkerCloseToken(pick))));

    private static readonly Gen<SegmentPlan> Segment =
        from ignorable in Gen.Int[0, 4].Select(i => i == 0)
        from hasId in Gen.Bool
        from state in Gen.Enum<SegmentState>()
        from subState in Gen.OneOfConst<string?>(null, null, "cat:locked", "x:\t y")
        from canResegment in OptionalBool
        from source in Token.Array[0, 5]
        from target in Gen.Null(Token.Array[0, 5], 0.4)
        from orderKey in Gen.Nullable(Gen.Int[0, 100], 0.7)
        select new SegmentPlan(ignorable, hasId, state, subState, canResegment, source, target, orderKey);

    private static readonly Gen<ItemAttrs> Item =
        from name in OptionalText
        from type in Gen.OneOfConst<string?>(null, null, "my:dialog")
        from canResegment in OptionalBool
        from translate in OptionalBool
        from sourceDir in Direction
        from targetDir in Direction
        from attributes in ExtensionAttributes
        select new ItemAttrs(name, type, canResegment, translate, sourceDir, targetDir, attributes);

    private static readonly Gen<UnitPlan> Unit =
        from attributes in Item
        from notes in Note.Array[0, 2]
        from extensions in Extensions
        from segments in Segment.Array[1, 3]
        from data in ContentText.Array[1, 3]
        select new UnitPlan(attributes, notes, extensions, segments, data);

    private static Gen<ItemPlan> ItemPlan(int depth) =>
        depth == 0
            ? Unit.Select(u => (ItemPlan)u)
            : Gen.Frequency(
                (3, Unit.Select(u => (ItemPlan)u)),
                (1, from attributes in Item
                    from notes in Note.Array[0, 2]
                    from extensions in Extensions
                    from items in ItemPlan(depth - 1).Array[0, 3]
                    select (ItemPlan)new GroupPlan(attributes, notes, extensions, items)));

    private static readonly Gen<FilePlan> File =
        from original in OptionalText
        from canResegment in OptionalBool
        from translate in OptionalBool
        from sourceDir in Direction
        from targetDir in Direction
        from skeleton in Skeleton
        from notes in Note.Array[0, 2]
        from extensions in Extensions
        from attributes in ExtensionAttributes
        from items in ItemPlan(2).Array[1, 3]
        select new FilePlan(original, canResegment, translate, sourceDir, targetDir, skeleton, notes, extensions, attributes, items);

    public static readonly Gen<DocumentPlan> DocumentPlan =
        from sourceLanguage in Gen.OneOfConst(Languages)
        from targetLanguage in Gen.Null(Gen.OneOfConst(Languages), 0.3)
        from attributes in ExtensionAttributes
        from files in File.Array[1, 2]
        select new DocumentPlan(sourceLanguage, targetLanguage, attributes, files);

    /// <summary>Valid documents.</summary>
    public static readonly Gen<TranslationDocument> Document = DocumentPlan.Select(DocumentBuilder.Build);
}

public sealed record ExtensionAttrPlan(int Namespace, int Name, string Value);

public sealed record NotePlan(
    bool HasId, string Text, string? Category, int? Priority, NoteAppliesTo? AppliesTo, ExtensionAttrPlan[] Attributes);

public sealed record CodeAttrs(
    string? Type, string? SubType, string? CopyOf, bool CanCopy, bool CanDelete, CanReorder CanReorder, bool CanOverlap,
    string? Dir, int? DataRef, string? Disp, string? Equiv, string? SubFlows, ExtensionAttrPlan[] Attributes);

public sealed record MarkerAttrs(bool? Translate, string? Type, string? Ref, string? Value, ExtensionAttrPlan[] Attributes);

public abstract record Token;

public sealed record TextToken(string Text) : Token;

public sealed record StandaloneToken(CodeAttrs Code) : Token;

public sealed record OpenToken(CodeAttrs Code) : Token;

/// <summary>Closes one of the open pairs (chosen by <paramref name="Pick"/>); ignored if none is open.</summary>
public sealed record CloseToken(CodeAttrs Code, int Pick, bool MatchesOpening) : Token;

public sealed record IsolatedToken(CodeAttrs Code, bool IsOpening) : Token;

public sealed record MarkerOpenToken(MarkerAttrs Marker) : Token;

public sealed record MarkerCloseToken(int Pick) : Token;

public sealed record SegmentPlan(
    bool Ignorable, bool HasId, SegmentState State, string? SubState, bool? CanResegment,
    Token[] Source, Token[]? Target, int? OrderKey);

public sealed record ItemAttrs(
    string? Name, string? Type, bool? CanResegment, bool? Translate, string? SourceDir, string? TargetDir,
    ExtensionAttrPlan[] Attributes);

public abstract record ItemPlan(ItemAttrs Attributes, NotePlan[] Notes, XElement[] Extensions);

public sealed record UnitPlan(ItemAttrs Attributes, NotePlan[] Notes, XElement[] Extensions, SegmentPlan[] Segments, string[] Data)
    : ItemPlan(Attributes, Notes, Extensions);

public sealed record GroupPlan(ItemAttrs Attributes, NotePlan[] Notes, XElement[] Extensions, ItemPlan[] Items)
    : ItemPlan(Attributes, Notes, Extensions);

public sealed record FilePlan(
    string? Original, bool? CanResegment, bool? Translate, string? SourceDir, string? TargetDir, XElement? Skeleton,
    NotePlan[] Notes, XElement[] Extensions, ExtensionAttrPlan[] Attributes, ItemPlan[] Items);

public sealed record DocumentPlan(string SourceLanguage, string? TargetLanguage, ExtensionAttrPlan[] Attributes, FilePlan[] Files);
