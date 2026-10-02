using System.Xml.Linq;

namespace SchrodingersCat.Core.Models;

/// <summary>
/// One translation job for a single language pair — the in-memory form of an XLIFF file.
/// </summary>
public sealed class TranslationDocument
{
    /// <summary>BCP 47 language tag, e.g. <c>en-US</c>.</summary>
    public required string SourceLanguage { get; set; }

    /// <summary>BCP 47 language tag, e.g. <c>hu-HU</c>. Optional in XLIFF, required once targets exist.</summary>
    public string? TargetLanguage { get; set; }

    public List<TranslationFile> Files { get; } = [];

    /// <summary>Attributes from other namespaces on <c>&lt;xliff&gt;</c>, preserved as-is.</summary>
    public List<ExtensionAttr> ExtensionAttributes { get; } = [];

    public IEnumerable<TranslationUnit> Units => Files.SelectMany(f => f.Units);

    public IEnumerable<Segment> TranslatableSegments => Units.SelectMany(u => u.TranslatableSegments);
}

/// <summary>
/// One original source file (e.g. <c>strings.resx</c>) inside a <see cref="TranslationDocument"/>.
/// Corresponds to an XLIFF <c>&lt;file&gt;</c>.
/// </summary>
public sealed class TranslationFile
{
    public required string Id { get; set; }

    /// <summary>Path or name of the original file the content was extracted from.</summary>
    public string? Original { get; set; }

    /// <summary>XLIFF <c>canResegment</c>; <c>null</c> means not specified (the XLIFF default is yes).</summary>
    public bool? CanResegment { get; set; }

    /// <summary>XLIFF <c>translate</c>; <c>null</c> means not specified (the XLIFF default is yes).</summary>
    public bool? Translate { get; set; }

    /// <summary>XLIFF <c>srcDir</c>: <c>ltr</c>, <c>rtl</c> or <c>auto</c>.</summary>
    public string? SourceDir { get; set; }

    /// <summary>XLIFF <c>trgDir</c>: <c>ltr</c>, <c>rtl</c> or <c>auto</c>.</summary>
    public string? TargetDir { get; set; }

    /// <summary>The <c>&lt;skeleton&gt;</c> element, kept verbatim. Its content belongs to the tool that extracted the file.</summary>
    public XElement? Skeleton { get; set; }

    public List<Note> Notes { get; } = [];

    /// <summary>The file's units and groups, in document order.</summary>
    public List<TranslationItem> Items { get; } = [];

    /// <summary>All units in the file, including those inside (nested) groups, in document order.</summary>
    public IEnumerable<TranslationUnit> Units => TranslationItem.UnitsIn(Items);

    /// <summary>Module and extension elements (matches, glossary, metadata, …), kept verbatim.</summary>
    public List<XElement> ExtensionElements { get; } = [];

    /// <summary>Attributes from other namespaces, preserved as-is.</summary>
    public List<ExtensionAttr> ExtensionAttributes { get; } = [];
}
