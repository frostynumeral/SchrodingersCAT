namespace SchrodingersCat.Core.Models;

/// <summary>
/// One piece of segment content: text, a code or an annotation marker.
/// Content is a flat sequence; the halves of a pair are matched by <c>Id</c>, not by nesting.
/// </summary>
/// <remarks>
/// A flat sequence (rather than a tree) lets XLIFF's <c>&lt;pc&gt;</c> and <c>&lt;sc&gt;/&lt;ec&gt;</c>
/// load into the same model, and lets a pair span segments. See <c>docs/design/inline-codes.md</c>.
/// </remarks>
public abstract record InlineElement;

/// <summary>Translatable text. May contain characters that are invalid in XML; the writer encodes them as <c>&lt;cp&gt;</c>.</summary>
public sealed record InlineText : InlineElement
{
    public InlineText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    public string Text { get; }
}

public enum InlineCodeKind
{
    /// <summary>XLIFF <c>&lt;ph/&gt;</c>.</summary>
    Standalone,

    /// <summary>Start of a <c>&lt;pc&gt;</c>, or an <c>&lt;sc/&gt;</c>.</summary>
    Opening,

    /// <summary>End of a <c>&lt;pc&gt;</c>, or an <c>&lt;ec/&gt;</c>.</summary>
    Closing,
}

/// <summary>XLIFF 2.0 <c>canReorder</c>.</summary>
public enum CanReorder
{
    Yes,

    /// <summary>First code of a sequence that may not be reordered.</summary>
    FirstNo,

    No,
}

/// <summary>
/// An inline code (<c>&lt;ph&gt;</c>, <c>&lt;pc&gt;</c>, <c>&lt;sc&gt;</c>, <c>&lt;ec&gt;</c>): formatting,
/// a placeholder or any other native markup the translator must keep but not translate.
/// </summary>
/// <remarks>
/// The opening and closing halves of a pair share the same <see cref="Id"/>. Attributes that XLIFF splits
/// into start and end variants on <c>&lt;pc&gt;</c> (<c>dataRefStart</c>/<c>dataRefEnd</c>, …) live on the
/// respective half. Like XLIFF's <c>&lt;ec&gt;</c>, a closing half repeats the pair-level attributes
/// (<c>type</c>, <c>subType</c>, <c>canCopy</c>, …), so a <c>&lt;pc&gt;</c> loads into both halves.
/// </remarks>
public sealed record InlineCode : InlineElement
{
    private readonly EquatableList<ExtensionAttr> _extensionAttributes = EquatableList<ExtensionAttr>.Empty;

    public InlineCode(string id, InlineCodeKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        Id = id;
        Kind = kind;
    }

    /// <summary>Unique within the unit; shared by both halves of a pair.</summary>
    public string Id { get; }

    public InlineCodeKind Kind { get; }

    /// <summary>Key into <c>TranslationUnit.OriginalData</c>: the native code this stands for.</summary>
    public string? DataRef { get; init; }

    /// <summary><c>fmt</c>, <c>ui</c>, <c>quote</c>, <c>link</c>, <c>image</c> or <c>other</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Refinement of <see cref="Type"/> in <c>prefix:value</c> form, e.g. <c>xlf:b</c>.</summary>
    public string? SubType { get; init; }

    /// <summary>What the editor shows for the code, e.g. <c>{0}</c>.</summary>
    public string? Disp { get; init; }

    /// <summary>Plain-text equivalent, used for search, word counts and machine translation.</summary>
    public string? Equiv { get; init; }

    /// <summary>Id of the source code this one is a copy of.</summary>
    public string? CopyOf { get; init; }

    /// <summary>Ids of units holding text that belongs to this code (e.g. an image's alt text). Kept verbatim.</summary>
    public string? SubFlows { get; init; }

    /// <summary><c>ltr</c>, <c>rtl</c> or <c>auto</c>.</summary>
    public string? Dir { get; init; }

    public bool CanCopy { get; init; } = true;

    public bool CanDelete { get; init; } = true;

    public CanReorder CanReorder { get; init; } = CanReorder.Yes;

    /// <summary>
    /// <c>null</c> for standalone codes, where it does not apply. The reader fills in the element's XLIFF default
    /// (<c>no</c> for <c>&lt;pc&gt;</c>, <c>yes</c> for <c>&lt;sc&gt;</c>/<c>&lt;ec&gt;</c>), so the two forms can't be confused.
    /// </summary>
    public bool? CanOverlap { get; init; }

    /// <summary><c>true</c> if the other half of the pair is outside this unit.</summary>
    public bool IsIsolated { get; init; }

    /// <summary>Attributes from other namespaces (modules, other tools), preserved as-is.</summary>
    public IReadOnlyList<ExtensionAttr> ExtensionAttributes
    {
        get => _extensionAttributes;
        init => _extensionAttributes = new EquatableList<ExtensionAttr>(value);
    }
}

public enum InlineMarkerKind
{
    /// <summary>Start of a <c>&lt;mrk&gt;</c>, or an <c>&lt;sm/&gt;</c>.</summary>
    Opening,

    /// <summary>End of a <c>&lt;mrk&gt;</c>, or an <c>&lt;em/&gt;</c>.</summary>
    Closing,
}

/// <summary>
/// An annotation over a span of content (<c>&lt;mrk&gt;</c>, <c>&lt;sm&gt;</c>, <c>&lt;em&gt;</c>):
/// a term, a comment, or a span that must not be translated.
/// </summary>
/// <remarks>Attributes are only meaningful on the opening half; XLIFF's closing markers carry none.</remarks>
public sealed record InlineMarker : InlineElement
{
    private readonly EquatableList<ExtensionAttr> _extensionAttributes = EquatableList<ExtensionAttr>.Empty;

    public InlineMarker(string id, InlineMarkerKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        Id = id;
        Kind = kind;
    }

    /// <summary>Unique within the unit; shared by both halves.</summary>
    public string Id { get; }

    public InlineMarkerKind Kind { get; }

    /// <summary><c>false</c> means the span must not be translated; <c>null</c> means inherited.</summary>
    public bool? Translate { get; init; }

    /// <summary><c>generic</c>, <c>comment</c>, <c>term</c> or a <c>prefix:value</c> custom type.</summary>
    public string? Type { get; init; }

    /// <summary>Reference to related data, e.g. a note or a term definition. Kept verbatim.</summary>
    public string? Ref { get; init; }

    public string? Value { get; init; }

    /// <summary>Attributes from other namespaces (modules, other tools), preserved as-is.</summary>
    public IReadOnlyList<ExtensionAttr> ExtensionAttributes
    {
        get => _extensionAttributes;
        init => _extensionAttributes = new EquatableList<ExtensionAttr>(value);
    }
}

/// <summary>An attribute outside the XLIFF core namespace, kept so a round-trip does not lose it.</summary>
public sealed record ExtensionAttr(string Namespace, string LocalName, string Value);
