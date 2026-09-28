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

    public List<TranslationUnit> Units { get; } = [];
}
