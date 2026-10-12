using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Memory;

public static partial class Tbx
{
    // DtdProcessing.Ignore skips a DOCTYPE without reading what it declares (open question 9): nothing is expanded or
    // fetched, and an entity only the DTD declares is an error.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CloseInput = false,
    };

    // The dates TBX files have: ISO 8601 with or without a time, fraction or offset; none means UTC.
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy-MM-dd"];

    /// <summary>
    /// Reads the entries of a TBX 2 or TBX 3 file for a termbase of these languages, in the file's order.
    /// </summary>
    /// <exception cref="TermbaseFormatException">The file isn't well-formed XML or isn't TBX, or is larger or nested
    /// deeper than the limits.</exception>
    public static async Task<TermImport> ReadAsync(
        Stream input,
        string sourceLanguage,
        string targetLanguage,
        long maxInputBytes = DefaultMaxInputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        var entries = new List<TermEntry>();
        var skipped = new List<string>();
        var notices = new List<string>();
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var reader = new DepthLimitingXmlReader(
                XmlReader.Create(new LimitedReadStream(input, maxInputBytes, Refused), ReaderSettings), MaxElementDepth, Refused);

            await reader.MoveToContentAsync();
            var dialect = reader.NodeType != XmlNodeType.Element ? null
                : reader.LocalName == "martif" && reader.NamespaceURI.Length == 0 ? Dialect.Tbx2
                : reader.LocalName == "tbx" && reader.NamespaceURI == Tbx3Namespace.NamespaceName ? Dialect.Tbx3
                : null;
            if (dialect is null)
                throw new TermbaseFormatException("The file isn't TBX: its root element isn't <martif> (TBX 2) or <tbx> in TBX 3's namespace.");

            var number = 0;
            await reader.ReadAsync();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == dialect.Entry && reader.NamespaceURI == dialect.Ns.NamespaceName)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    number++;
                    var element = (XElement)await XNode.ReadFromAsync(reader, cancellationToken);
                    var entryReader = new EntryReader(dialect, sourceLanguage, targetLanguage, number);
                    try
                    {
                        entries.Add(entryReader.Read(element));
                        notices.AddRange(entryReader.Notices);
                        foreach (var (category, count) in entryReader.Dropped)
                            dropped[category] = dropped.GetValueOrDefault(category) + count;
                    }
                    catch (SkipException ex)
                    {
                        skipped.Add($"Entry {number}: {ex.Message}");
                    }
                }
                else
                {
                    await reader.ReadAsync();
                }
            }
        }
        catch (XmlException ex)
        {
            throw new TermbaseFormatException($"The file is not well-formed XML: {ex.Message}", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new TermbaseFormatException($"The file is not well-formed XML: {ex.Message}", ex);
        }

        return new TermImport(entries, skipped, notices, dropped);
    }

    private static TermbaseFormatException Refused(string message) => new(message);

    private sealed class SkipException(string message) : Exception(message);

    /// <summary>Reads one entry; what it notices and drops is merged into the import's report only if it's kept.</summary>
    private sealed class EntryReader(Dialect dialect, string sourceLanguage, string targetLanguage, int number)
    {
        private readonly XNamespace _ns = dialect.Ns;
        private readonly List<(string Type, string? Date, string? User)> _transactions = [];

        public List<string> Notices { get; } = [];

        public Dictionary<string, int> Dropped { get; } = new(StringComparer.Ordinal);

        public TermEntry Read(XElement entry)
        {
            var langSets = entry.Elements(_ns + dialect.LangSet).ToList();
            foreach (var langSet in langSets)
            {
                if (Lang(langSet) is null)
                    throw new SkipException($"a <{dialect.LangSet}> has no xml:lang.");
            }

            var sourceTag = Choose(langSets, sourceLanguage);
            var targetTag = Choose(langSets, targetLanguage);
            var source = new List<Term>();
            var target = new List<Term>();
            var notes = new List<string>();
            var others = false;
            foreach (var child in entry.Elements())
            {
                if (child.Name != _ns + dialect.LangSet)
                {
                    Aux(child, notes, "");
                    continue;
                }

                var tag = Lang(child)!;
                if (string.Equals(tag, sourceTag, StringComparison.OrdinalIgnoreCase))
                {
                    ReadLangSet(child, source, notes, tag);
                }
                else if (string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase))
                {
                    ReadLangSet(child, target, notes, tag);
                }
                else
                {
                    others = true;
                    Drop($"{dialect.LangSet} xml:lang=\"{tag}\"");
                }
            }

            if (source.Count == 0)
                throw new SkipException($"no {sourceLanguage} term.");
            if (targetTag is null && others)
                throw new SkipException($"no {targetLanguage} term.");

            var (changed, changedBy) = Transaction();
            return new TermEntry(source, target, notes.Count == 0 ? null : string.Join('\n', notes), changed, changedBy);
        }

        private static string? Lang(XElement langSet) => (string?)langSet.Attribute(XmlNs + "lang");

        // The exact language first, then the first regional variant of it.
        private static string? Choose(List<XElement> langSets, string language) =>
            langSets.Select(Lang).FirstOrDefault(tag => string.Equals(tag, language, StringComparison.OrdinalIgnoreCase))
            ?? langSets.Select(Lang).FirstOrDefault(tag => TmLanguages.Serves(tag!, language));

        private void ReadLangSet(XElement langSet, List<Term> terms, List<string> notes, string tag)
        {
            foreach (var child in langSet.Elements())
            {
                if (dialect.IsTbx3 ? child.Name == _ns + "termSec" : child.Name == _ns + "tig")
                {
                    terms.Add(ReadTerm(child, []));
                }
                else if (!dialect.IsTbx3 && child.Name == _ns + "ntig")
                {
                    var groups = child.Elements(_ns + "termGrp").ToList();
                    if (groups.Count != 1)
                        throw new SkipException(groups.Count == 0 ? "an <ntig> has no <termGrp>." : "an <ntig> has two <termGrp>s.");
                    terms.Add(ReadTerm(groups[0], child.Elements().Where(e => e != groups[0])));
                }
                else
                {
                    Aux(child, notes, $"{tag}: ");
                }
            }
        }

        // A tig, termGrp or termSec: the term, its status and its notes. around: the rest of an ntig, beside its termGrp.
        private Term ReadTerm(XElement holder, IEnumerable<XElement> around)
        {
            var termElements = holder.Elements(_ns + "term").ToList();
            if (termElements.Count != 1)
                throw new SkipException(termElements.Count == 0 ? $"a <{holder.Name.LocalName}> has no <term>." : $"a <{holder.Name.LocalName}> has two <term>s.");

            var text = termElements[0].Value;
            string? administrative = null;
            string? normative = null;
            var notes = new List<string>();

            void Status(XElement element)
            {
                if (element.Name == _ns + "termNote" && (string?)element.Attribute("type") == "administrativeStatus"
                    || element.Name == MinNamespace + "administrativeStatus")
                {
                    administrative ??= element.Value;
                }
                else if (element.Name == _ns + "termNote" && (string?)element.Attribute("type") == "normativeAuthorization")
                {
                    normative ??= element.Value;
                }
                else if (element.Name == _ns + "termNoteGrp")
                {
                    foreach (var child in element.Elements())
                        Status(child);
                }
                else
                {
                    Aux(element, notes, "", termLevel: true);
                }
            }

            foreach (var child in holder.Elements().Where(e => e != termElements[0]).Concat(around))
                Status(child);

            var status = TermStatus.Unspecified;
            if ((administrative ?? normative) is { } value)
            {
                if (TermStatusNames.ParseTbx(value) is { } known)
                    status = known;
                else
                    Notices.Add($"Entry {number}: the status \"{value}\" of \"{text}\" isn't known; the term is imported without one.");
            }

            return new Term(text, status) { Note = notes.Count == 0 ? null : string.Join('\n', notes) };
        }

        // What sits beside the terms: notes and definitions (a term's definition is dropped: definitions are about the
        // concept), transactions, and the data categories that are dropped.
        private void Aux(XElement element, List<string> notes, string prefix, bool termLevel = false)
        {
            if (element.Name == _ns + "note")
            {
                notes.Add(prefix + element.Value);
            }
            else if (IsDefinition(element) && !termLevel)
            {
                notes.Add(prefix + element.Value);
            }
            else if (element.Name == _ns + "descripGrp")
            {
                foreach (var child in element.Elements())
                {
                    if (IsDefinition(child) && !termLevel)
                        notes.Add(prefix + child.Value);
                    else
                        Drop(Category(child));
                }
            }
            else if (element.Name == _ns + "transacGrp")
            {
                Transaction(element);
            }
            else
            {
                Drop(Category(element));
            }
        }

        private bool IsDefinition(XElement element) =>
            element.Name == _ns + "descrip" && (string?)element.Attribute("type") == "definition"
            || element.Name == BasicNamespace + "definition";

        // A DCA element by its type, anything else by its name.
        private string Category(XElement element) =>
            element.Name.Namespace == _ns && (string?)element.Attribute("type") is { } type ? type : element.Name.LocalName;

        private void Drop(string category) => Dropped[category] = Dropped.GetValueOrDefault(category) + 1;

        private void Transaction(XElement group)
        {
            var type = group.Elements(_ns + "transac").FirstOrDefault(e => (string?)e.Attribute("type") == "transactionType")?.Value
                ?? group.Element(BasicNamespace + "transactionType")?.Value;
            var user = group.Elements(_ns + "transacNote").FirstOrDefault(e => (string?)e.Attribute("type") == "responsibility")?.Value
                ?? group.Element(BasicNamespace + "responsibility")?.Value;
            if (type?.Trim() is { } kind and ("modification" or "origination"))
                _transactions.Add((kind, group.Element(_ns + "date")?.Value, user));
        }

        // The latest modification (concept, language and term levels alike; document order says nothing), else the
        // latest origination; a transaction without a readable date only if none has one. A date that can't be read is
        // reported if it could have been the entry's.
        private (DateTimeOffset Changed, string? ChangedBy) Transaction()
        {
            foreach (var type in new[] { "modification", "origination" })
            {
                (DateTimeOffset Date, string? User)? latest = null;
                (string? Date, string? User)? undated = null;
                foreach (var (kind, date, user) in _transactions)
                {
                    if (kind != type)
                        continue;
                    if (date is not null && DateTimeOffset.TryParseExact(
                        date.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var changed))
                    {
                        if (latest is null || changed >= latest.Value.Date)
                            latest = (changed, user);
                    }
                    else
                    {
                        undated = (date, user);
                    }
                }

                if (latest is { } found)
                    return found;
                if (undated is { } last)
                {
                    foreach (var (kind, date, _) in _transactions)
                    {
                        if (kind == type && date is not null)
                            Notices.Add($"Entry {number}: the date \"{date}\" can't be read; the entry is imported without one.");
                    }

                    return (default, last.User);
                }
            }

            return (default, null);
        }
    }
}
