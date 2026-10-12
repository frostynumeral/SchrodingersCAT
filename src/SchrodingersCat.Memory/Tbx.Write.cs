using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SchrodingersCat.Memory;

public static partial class Tbx
{
    /// <summary>
    /// Writes the entries as TBX-Basic, TBX 2 unless asked for TBX 3. Entries with text XML can't hold (lone
    /// surrogates, control characters) are skipped and reported.
    /// </summary>
    public static async Task<TermExport> WriteAsync(
        Stream output,
        IEnumerable<TermEntry> entries,
        string sourceLanguage,
        string targetLanguage,
        TbxVersion version = TbxVersion.Tbx2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);
        if (!Enum.IsDefined(version))
            throw new ArgumentOutOfRangeException(nameof(version), version, "Not a TBX version.");

        var dialect = version == TbxVersion.Tbx3 ? Dialect.Tbx3 : Dialect.Tbx2;
        var ns = dialect.Ns;
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineHandling = NewLineHandling.Entitize,
            CloseOutput = false,
        };

        var written = 0;
        var skipped = new List<string>();
        await using var writer = XmlWriter.Create(output, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, dialect.IsTbx3 ? "tbx" : "martif", ns.NamespaceName);
        await writer.WriteAttributeStringAsync(null, "type", null, "TBX-Basic");
        if (dialect.IsTbx3)
            await writer.WriteAttributeStringAsync(null, "style", null, "dca");
        await writer.WriteAttributeStringAsync("xml", "lang", null, sourceLanguage);

        var fileDesc = new XElement(ns + "fileDesc", new XElement(ns + "sourceDesc", new XElement(ns + "p", "SchrodingersCAT")));
        var header = dialect.IsTbx3
            ? new XElement(ns + "tbxHeader", fileDesc)
            : new XElement(ns + "martifHeader", fileDesc, new XElement(ns + "encodingDesc", new XElement(ns + "p", new XAttribute("type", "XCSURI"), "TBXBasicXCSV02.xcs")));
        await header.WriteToAsync(writer, cancellationToken);
        await writer.WriteStartElementAsync(null, "text", ns.NamespaceName);
        await writer.WriteStartElementAsync(null, "body", ns.NamespaceName);

        var number = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            number++;

            // XML can't hold every string an entry can (e.g. lone surrogates); check before writing.
            if (!IsWritable(entry))
            {
                skipped.Add($"Entry {number}: its text has characters XML can't hold.");
                continue;
            }

            await Entry(dialect, entry, ++written, sourceLanguage, targetLanguage).WriteToAsync(writer, cancellationToken);
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        return new TermExport(written, skipped, []);
    }

    private static XElement Entry(Dialect dialect, TermEntry entry, int id, string sourceLanguage, string targetLanguage)
    {
        var ns = dialect.Ns;
        var element = new XElement(ns + dialect.Entry, new XAttribute("id", $"c{id}"));

        // The entry's one date and user, as its last modification; TBX has no place for a user without a transaction.
        if (entry.Changed != default || entry.ChangedBy is not null)
        {
            var transaction = new XElement(ns + "transacGrp", new XElement(ns + "transac", new XAttribute("type", "transactionType"), "modification"));
            if (entry.Changed != default)
                transaction.Add(new XElement(ns + "date", Date(entry.Changed)));
            if (entry.ChangedBy is not null)
                transaction.Add(new XElement(ns + "transacNote", new XAttribute("type", "responsibility"), entry.ChangedBy));
            element.Add(transaction);
        }

        if (entry.Note is not null)
            element.Add(new XElement(ns + "note", entry.Note));

        // A langSet needs a term, so an entry without translations has only the source's.
        element.Add(LangSet(dialect, entry.Source, sourceLanguage));
        if (entry.Target.Count > 0)
            element.Add(LangSet(dialect, entry.Target, targetLanguage));
        return element;
    }

    private static XElement LangSet(Dialect dialect, IReadOnlyList<Term> terms, string language)
    {
        var ns = dialect.Ns;
        var langSet = new XElement(ns + dialect.LangSet, new XAttribute(XmlNs + "lang", language));
        foreach (var term in terms)
        {
            var holder = new XElement(ns + (dialect.IsTbx3 ? "termSec" : "tig"), new XElement(ns + "term", term.Text));
            if (TermStatusNames.AdministrativeStatus(term.Status) is { } status)
                holder.Add(new XElement(ns + "termNote", new XAttribute("type", "administrativeStatus"), status));
            if (term.Note is not null)
                holder.Add(new XElement(ns + "note", term.Note));
            langSet.Add(holder);
        }

        return langSet;
    }

    // UTC, with the fraction only when there is one, so the import gives back the same instant.
    private static string Date(DateTimeOffset changed) =>
        changed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static bool IsWritable(TermEntry entry)
    {
        var texts = entry.Source.Concat(entry.Target).SelectMany(t => new[] { t.Text, t.Note })
            .Concat([entry.Note, entry.ChangedBy]).OfType<string>();
        foreach (var text in texts)
        {
            try
            {
                XmlConvert.VerifyXmlChars(text);
            }
            catch (XmlException)
            {
                return false;
            }
        }

        return true;
    }
}
