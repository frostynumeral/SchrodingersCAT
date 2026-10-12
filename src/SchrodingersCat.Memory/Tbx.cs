using System.Xml.Linq;

namespace SchrodingersCat.Memory;

/// <summary>The TBX version an export writes.</summary>
public enum TbxVersion
{
    /// <summary>TBX 2 (ISO 30042:2008, <c>&lt;martif&gt;</c>) with TBX-Basic's data categories: what most tools import.</summary>
    Tbx2,

    /// <summary>TBX 3 (ISO 30042:2019, <c>&lt;tbx&gt;</c>), TBX-Basic in the DCA style.</summary>
    Tbx3,
}

/// <summary>
/// TBX import and export (section 4 of <c>docs/design/termbase.md</c>), the format every CAT tool reads and writes
/// terms in.
/// </summary>
/// <remarks>
/// <para><b>Import</b> reads TBX 2 (<c>termEntry</c> → <c>langSet</c> → <c>tig</c> or <c>ntig</c>/<c>termGrp</c> →
/// <c>term</c>) and TBX 3 (<c>conceptEntry</c> → <c>langSec</c> → <c>termSec</c> → <c>term</c>), in both of TBX 3's
/// styles whatever the file says it is: DCA (<c>termNote type="administrativeStatus"</c>) and DCT (an element per data
/// category, matched by namespace URI and local name, never by prefix). It keeps terms, their status and notes, the
/// concept's notes and definitions (those of the two languages prefixed with the language), and the latest
/// modification's (or else origination's) date and user, at any level. Everything else is dropped and counted.</para>
/// <para><b>Languages:</b> the termbase's two languages are taken, regional variants included by the TM's rule
/// (<see cref="TmLanguages"/>): the exact tag if an entry has it, else the first variant. An entry without a source
/// term, or with terms in other languages but none in the target language, is skipped and reported. An entry with only
/// source terms is kept with no translations, as an export writes an entry with none.</para>
/// <para><b>Untrusted input:</b> the TMX reader's defences (a size limit, a nesting limit, no external resolver), with
/// one difference: a DOCTYPE, which most TBX 2 files have, is skipped without being processed, so no entity is ever
/// expanded and nothing is fetched; an entity only the DTD defines fails as malformed XML. A file that isn't TBX fails
/// with <see cref="TermbaseFormatException"/>; an entry it can't use is skipped and reported.</para>
/// <para><b>Export</b> writes TBX-Basic, without a DOCTYPE: terms with their status (<see cref="TermStatus.Unspecified"/>
/// writes none) and notes, the entry's note, and its date and user as a modification. An entry with text XML can't
/// hold is skipped and reported. Exporting and importing again gives back the same entries.</para>
/// </remarks>
public static partial class Tbx
{
    /// <summary>The default size limit for imports.</summary>
    public const long DefaultMaxInputBytes = 512L * 1024 * 1024;

    /// <summary>The nesting limit for imports.</summary>
    public const int MaxElementDepth = 64;

    /// <summary>TBX 3's namespace.</summary>
    internal static readonly XNamespace Tbx3Namespace = "urn:iso:std:iso:30042:ed-2";

    /// <summary>The TBX-Min module's namespace, which DCT files take <c>administrativeStatus</c> from.</summary>
    internal static readonly XNamespace MinNamespace = "http://www.tbxinfo.net/ns/min";

    /// <summary>The TBX-Basic module's namespace, which DCT files take <c>definition</c> and the transactions from.</summary>
    internal static readonly XNamespace BasicNamespace = "http://www.tbxinfo.net/ns/basic";

    private static readonly XNamespace XmlNs = XNamespace.Xml;

    // The element names that differ between the versions; the others are the same in both, in the version's namespace.
    private sealed record Dialect(XNamespace Ns, string Entry, string LangSet)
    {
        public static readonly Dialect Tbx2 = new(XNamespace.None, "termEntry", "langSet");

        public static readonly Dialect Tbx3 = new(Tbx3Namespace, "conceptEntry", "langSec");

        public bool IsTbx3 => Ns == Tbx3Namespace;
    }
}
