using System.Xml.Linq;

namespace SchrodingersCat.Core.Models;

/// <summary>
/// What a <see cref="TranslationFile"/> or <see cref="TranslationGroup"/> contains: a <see cref="TranslationUnit"/>
/// or a nested <see cref="TranslationGroup"/>. Holds what XLIFF's <c>&lt;unit&gt;</c> and <c>&lt;group&gt;</c> share.
/// </summary>
public abstract class TranslationItem
{
    private protected TranslationItem()
    {
    }

    /// <summary>Required. Unit ids are unique among the file's units, group ids among its groups.</summary>
    public required string Id { get; set; }

    /// <summary>Optional human-readable name, e.g. the resource key.</summary>
    public string? Name { get; set; }

    /// <summary>XLIFF <c>type</c> in <c>prefix:value</c> form.</summary>
    public string? Type { get; set; }

    /// <summary>XLIFF <c>canResegment</c>; <c>null</c> means inherited.</summary>
    public bool? CanResegment { get; set; }

    /// <summary>XLIFF <c>translate</c>; <c>null</c> means inherited.</summary>
    public bool? Translate { get; set; }

    /// <summary>XLIFF <c>srcDir</c>: <c>ltr</c>, <c>rtl</c> or <c>auto</c>; <c>null</c> means inherited.</summary>
    public string? SourceDir { get; set; }

    /// <summary>XLIFF <c>trgDir</c>: <c>ltr</c>, <c>rtl</c> or <c>auto</c>; <c>null</c> means inherited.</summary>
    public string? TargetDir { get; set; }

    public List<Note> Notes { get; } = [];

    /// <summary>Module and extension elements (matches, glossary, metadata, …), kept verbatim.</summary>
    public List<XElement> ExtensionElements { get; } = [];

    /// <summary>Attributes from other namespaces, preserved as-is.</summary>
    public List<ExtensionAttr> ExtensionAttributes { get; } = [];

    internal static IEnumerable<TranslationUnit> UnitsIn(IEnumerable<TranslationItem> items) =>
        items.SelectMany(item => item switch
        {
            TranslationUnit unit => [unit],
            TranslationGroup group => group.Units,
            _ => throw new InvalidOperationException($"Unknown item type {item.GetType()}."),
        });
}

/// <summary>A group of units and nested groups (XLIFF <c>&lt;group&gt;</c>), e.g. one dialog or one resource section.</summary>
public sealed class TranslationGroup : TranslationItem
{
    /// <summary>The group's units and nested groups, in document order.</summary>
    public List<TranslationItem> Items { get; } = [];

    /// <summary>All units in the group, including those in nested groups, in document order.</summary>
    public IEnumerable<TranslationUnit> Units => UnitsIn(Items);
}
