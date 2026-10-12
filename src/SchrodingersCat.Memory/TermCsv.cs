using System.Globalization;
using System.Text;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Memory;

/// <summary>A CSV file split into rows and cells, and how it was read.</summary>
/// <param name="Separator">The field separator found in the first row: <c>,</c>, <c>;</c> or a tab.</param>
public sealed record CsvTable(char Separator, Encoding Encoding, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Which columns of a CSV table hold what; 0 is the first. A column a row doesn't have counts as empty.</summary>
public sealed record CsvColumns(int Source, int Target, int? Note, int? Status);

/// <summary>
/// CSV import and export of term entries (section 4 of <c>docs/design/termbase.md</c>): clients' spreadsheets.
/// </summary>
/// <remarks>
/// <para><b>Import</b> goes in steps, so the import dialog can show each: <see cref="ReadBytesAsync"/> (with the size
/// limit), <see cref="DetectEncoding"/> (null: ask the user, and preview with <see cref="LegacyEncoding"/>),
/// <see cref="Parse"/>, <see cref="GuessColumns"/> from the header row, and <see cref="Import"/>. Bytes that aren't
/// valid in the encoding are never replaced: <see cref="Parse"/> fails instead. A row is one entry: its source and
/// target cells hold terms separated by <c>|</c>, its status cell applies to every term in it.</para>
/// <para><b>Formulas:</b> the export puts a <c>'</c> before a cell that starts with <c>=</c>, <c>+</c>, <c>-</c> or
/// <c>@</c> (after any <c>'</c>s), so a spreadsheet doesn't run it; the import takes one <c>'</c> off a cell that
/// starts with <c>'</c>s and then one of those, so a client's own <c>'til</c> stays as it is, both ways.</para>
/// <para><b>Export</b> writes UTF-8 with a BOM (so Excel reads it as UTF-8), every field quoted, CRLF, a header row
/// of the two languages, <c>Note</c> and <c>Status</c>. CSV holds one note and one status per row, and no <c>|</c>
/// in a term, so what it can't hold is left out and reported: terms with <c>|</c>, blank terms, mixed statuses, the
/// terms' own notes, and whitespace around terms (which the import trims). Dates and users aren't written.</para>
/// </remarks>
public static class TermCsv
{
    /// <summary>The default size limit for imports.</summary>
    public const long DefaultMaxInputBytes = 256L * 1024 * 1024;

    /// <summary>
    /// The default limit on cells (a blank row counts as one), e.g. a million rows of five columns. The size limit alone
    /// doesn't bound memory: each cell and row costs tens of bytes however short it is, so a file of separators or
    /// line breaks would take a hundred times its size.
    /// </summary>
    public const int DefaultMaxCells = 5_000_000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    private static readonly IReadOnlyList<string> BlankRow = Array.AsReadOnly(new[] { "" });

    private static readonly string[] NoteHeaders = ["note", "notes", "comment", "comments", "definition"];

    // ---------------------------------------------------------------- import

    /// <summary>Reads the whole file, up to the size limit.</summary>
    /// <exception cref="TermbaseFormatException">The file is larger than the limit.</exception>
    public static async Task<byte[]> ReadBytesAsync(Stream input, long maxInputBytes = DefaultMaxInputBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var buffer = new MemoryStream();
        await new LimitedReadStream(input, maxInputBytes, message => new TermbaseFormatException(message)).CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// The encoding a BOM says (UTF-8, UTF-16 little- or big-endian); without one, UTF-8 if the bytes are valid UTF-8;
    /// else null, and the user has to say.
    /// </summary>
    public static Encoding? DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (Bom(bytes) is { } bom)
            return bom.Encoding;

        try
        {
            StrictUtf8.GetCharCount(bytes);
            return StrictUtf8;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A legacy (Windows or ISO) code page, e.g. 1250 for Central European, without registering it globally.</summary>
    public static Encoding LegacyEncoding(int codePage) =>
        CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);

    /// <summary>
    /// Splits the file into rows and cells (RFC 4180: quoted fields, doubled quotes, line breaks inside quotes; rows end
    /// at CRLF, LF or CR). A BOM decides the encoding, whatever <paramref name="encoding"/> says; without one, it is
    /// <paramref name="encoding"/>, or UTF-8. The separator is the one of tab, <c>;</c> and <c>,</c> the first row has
    /// most of outside quotes (on a tie, in that order); a comma if it has none.
    /// </summary>
    /// <exception cref="TermbaseFormatException">
    /// The bytes aren't valid in the encoding, a quoted field isn't closed, or the file has more than
    /// <paramref name="maxCells"/> cells.
    /// </exception>
    public static CsvTable Parse(ReadOnlySpan<byte> bytes, Encoding? encoding = null, int maxCells = DefaultMaxCells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCells);

        Encoding used;
        if (Bom(bytes) is { } bom)
        {
            used = bom.Encoding;
            bytes = bytes[bom.Length..];
        }
        else
        {
            used = encoding ?? StrictUtf8;
        }

        // Never replace what doesn't decode: the user chooses another encoding instead.
        var strict = (Encoding)used.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        string text;
        try
        {
            text = strict.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new TermbaseFormatException(
                encoding is null || used != encoding
                    ? $"The file isn't valid {used.WebName}; choose its encoding."
                    : $"The file isn't valid {used.WebName}.",
                ex);
        }

        var separator = DetectSeparator(text);
        return new CsvTable(separator, used, Split(text, separator, maxCells));
    }

    private static (Encoding Encoding, int Length)? Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? (StrictUtf8, 3)
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? (new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), 2)
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? (new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), 2)
        : null;

    private static char DetectSeparator(string text)
    {
        int tabs = 0, semicolons = 0, commas = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
                quoted = !quoted;
            else if (quoted)
                continue;
            else if (c is '\r' or '\n')
                break;
            else if (c == '\t')
                tabs++;
            else if (c == ';')
                semicolons++;
            else if (c == ',')
                commas++;
        }

        return tabs > 0 && tabs >= semicolons && tabs >= commas ? '\t'
            : semicolons > 0 && semicolons >= commas ? ';'
            : ',';
    }

    // Lenient where spreadsheets are sloppy: a quote inside an unquoted field, or after a closing quote, is text. Only
    // a quoted field that never closes fails, as the rest of the file would be one cell. Blank rows share one array.
    private static List<IReadOnlyList<string>> Split(string text, char separator, int maxCells)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var cells = 0;
        var i = 0;

        void AddCell(string value)
        {
            if (++cells > maxCells)
                throw new TermbaseFormatException($"The file has more than {maxCells.ToString("N0", CultureInfo.InvariantCulture)} cells.");
            row.Add(value);
        }

        void EndRow()
        {
            rows.Add(row is [""] ? BlankRow : row.ToArray());
            row.Clear();
        }

        while (i < text.Length)
        {
            // At the start of a cell.
            if (text[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i == text.Length)
                        throw new TermbaseFormatException($"A quoted field isn't closed (row {rows.Count + 1}).");
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    cell.Append(text[i++]);
                }
            }

            // The rest of the cell, up to a separator or the end of the row.
            while (i < text.Length && text[i] != separator && text[i] is not ('\r' or '\n'))
                cell.Append(text[i++]);

            AddCell(cell.ToString());
            cell.Clear();
            if (i == text.Length)
            {
                EndRow();
                break;
            }

            if (text[i] == separator)
            {
                i++;
                if (i == text.Length)
                {
                    AddCell("");
                    EndRow();
                }

                continue;
            }

            // A row's end: CRLF, LF or CR.
            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            EndRow();
        }

        return rows;
    }

    /// <summary>
    /// Which columns the header row names: a language tag (the language's own, its base language or a regional variant)
    /// or the language's English or native name for the source and target; <c>Note</c>, <c>Comment</c> or
    /// <c>Definition</c> for the note; <c>Status</c>. Null if it doesn't name both languages' columns.
    /// </summary>
    public static CsvColumns? GuessColumns(IReadOnlyList<string> header, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        var cells = header.Select(c => (c ?? "").Trim()).ToList();
        if (LanguageColumns(cells, sourceLanguage, targetLanguage) is not var (source, target))
            return null;

        return new CsvColumns(
            source,
            target,
            Find(cells, c => NoteHeaders.Contains(c, StringComparer.OrdinalIgnoreCase), source, target),
            Find(cells, c => string.Equals(c, "status", StringComparison.OrdinalIgnoreCase), source, target));
    }

    private static int? Find(List<string> cells, Func<string, bool> names, params int?[] taken)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (!taken.Contains(i) && names(cells[i]))
                return i;
        }

        return null;
    }

    // The source and target columns whose names fit best together: an exact name beats a loose one, so headers of two
    // regional variants (en and en-GB, Portuguese (Portugal) and Portuguese (Brazil)) aren't swapped. The best pair is
    // always among each language's two best columns; on a tie, the earlier source column, then target column.
    private static (int Source, int Target)? LanguageColumns(List<string> cells, string sourceLanguage, string targetLanguage)
    {
        var sources = Best(cells, sourceLanguage);
        var targets = Best(cells, targetLanguage);
        (int Source, int Target, int Fit)? best = null;
        foreach (var (s, sourceFit) in sources)
        {
            foreach (var (t, targetFit) in targets)
            {
                if (s == t)
                    continue;
                var fit = sourceFit + targetFit;
                if (best is not { } b || fit > b.Fit || fit == b.Fit && (s < b.Source || s == b.Source && t < b.Target))
                    best = (s, t, fit);
            }
        }

        return best is { } found ? (found.Source, found.Target) : null;
    }

    // The two columns that name the language best (by fit, then position).
    private static List<(int Column, int Fit)> Best(List<string> cells, string language) =>
        [.. cells.Select((cell, i) => (Column: i, Fit: NamesLanguage(cell, language)))
            .Where(c => c.Fit > 0)
            .OrderByDescending(c => c.Fit).ThenBy(c => c.Column)
            .Take(2)];

    // 2: the language's own tag or full name; 1: its base language, a regional variant, or its name without the region
    // ("Portuguese" for "Portuguese (Brazil)"); 0: not this language.
    private static int NamesLanguage(string cell, string language)
    {
        if (cell.Length == 0)
            return 0;
        if (string.Equals(cell, language, StringComparison.OrdinalIgnoreCase))
            return 2;

        var fit = TmLanguages.Serves(cell, language) || TmLanguages.Serves(language, cell) ? 1 : 0;
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var names = new[] { culture.EnglishName, culture.NativeName };
            if (names.Any(name => string.Equals(cell, name, StringComparison.OrdinalIgnoreCase)))
                return 2;
            var plain = BeforeParenthesis(cell);
            if (names.Any(name => string.Equals(plain, BeforeParenthesis(name), StringComparison.OrdinalIgnoreCase)))
                return 1;
        }
        catch (CultureNotFoundException)
        {
            // Without ICU (invariant globalization) only tags are recognised.
        }

        return fit;
    }

    private static string BeforeParenthesis(string name) =>
        name.IndexOf(" (", StringComparison.Ordinal) is var at and >= 0 ? name[..at] : name;

    /// <summary>
    /// The entries of the table's rows: the source and target cells' terms (split at <c>|</c> and trimmed), the status
    /// for all of them, and the note (null if empty). A blank row is passed over; a row without a source term is
    /// skipped and reported.
    /// </summary>
    public static TermImport Import(CsvTable table, CsvColumns columns, bool hasHeader = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        var entries = new List<TermEntry>();
        var skipped = new List<string>();
        var notices = new List<string>();
        for (var index = hasHeader ? 1 : 0; index < table.Rows.Count; index++)
        {
            var row = table.Rows[index];
            var number = index + 1;
            if (row.All(string.IsNullOrWhiteSpace))
                continue;

            string Cell(int? column) =>
                column is { } c && c >= 0 && c < row.Count && row[c] is { } value ? WithoutFormulaPrefix(value) : "";

            var status = TermStatus.Unspecified;
            var statusCell = Cell(columns.Status);
            if (!string.IsNullOrWhiteSpace(statusCell))
            {
                if (TermStatusNames.ParseCsv(statusCell) is { } known)
                    status = known;
                else
                    notices.Add($"Row {number}: the status \"{statusCell}\" isn't known; the terms are imported without one.");
            }

            var source = Terms(Cell(columns.Source), status);
            if (source.Count == 0)
            {
                skipped.Add($"Row {number}: no source term.");
                continue;
            }

            var note = Cell(columns.Note);
            entries.Add(new TermEntry(source, Terms(Cell(columns.Target), status), note.Length == 0 ? null : note, default, null));
        }

        return new TermImport(entries, skipped, notices, new Dictionary<string, int>());
    }

    private static List<Term> Terms(string cell, TermStatus status) =>
        [.. cell.Split('|').Select(t => t.Trim()).Where(t => t.Length > 0).Select(t => new Term(t, status))];

    // One ' off a cell that is 's and then a formula character: what the export (or a careful client) put there.
    private static string WithoutFormulaPrefix(string cell) =>
        IsFormula(cell, minQuotes: 1) ? cell[1..] : cell;

    // 's (at least minQuotes), then =, +, - or @.
    private static bool IsFormula(string cell, int minQuotes)
    {
        var quotes = 0;
        while (quotes < cell.Length && cell[quotes] == '\'')
            quotes++;
        return quotes >= minQuotes && quotes < cell.Length && cell[quotes] is '=' or '+' or '-' or '@';
    }

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Writes the entries as CSV. What CSV can't hold is left out and reported in <see cref="TermExport.Incomplete"/>;
    /// an entry left without a source term, or with text UTF-8 can't hold (lone surrogates), is skipped.
    /// </summary>
    public static async Task<TermExport> WriteAsync(
        Stream output,
        IEnumerable<TermEntry> entries,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);

        var written = 0;
        var skipped = new List<string>();
        var incomplete = new List<string>();
        await using var writer = new StreamWriter(output, Utf8WithBom, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(Row([sourceLanguage, targetLanguage, "Note", "Status"]));

        var number = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            number++;

            var lost = new List<string>();
            var source = Kept(entry.Source, lost);
            var target = Kept(entry.Target, lost);
            if (source.Count == 0)
            {
                skipped.Add($"Entry {number}: no source term CSV can hold.");
                continue;
            }

            var statuses = source.Concat(target).Select(t => t.Status).Distinct().ToList();
            if (statuses.Count > 1)
                lost.Add("the terms' different statuses");
            if (entry.Source.Concat(entry.Target).Any(t => t.Note is not null))
                lost.Add("the terms' notes");

            var cells = new[]
            {
                string.Join('|', source.Select(t => t.Text.Trim())),
                string.Join('|', target.Select(t => t.Text.Trim())),
                entry.Note ?? "",
                statuses.Count == 1 ? TermStatusNames.Word(statuses[0]) : "",
            };
            if (!cells.All(IsEncodable))
            {
                skipped.Add($"Entry {number}: its text has characters UTF-8 can't hold.");
                continue;
            }

            await writer.WriteLineAsync(Row(cells).AsMemory(), cancellationToken);
            written++;
            if (lost.Count > 0)
                incomplete.Add($"Entry {number}: written without {string.Join(", ", lost)}.");
        }

        await writer.FlushAsync(cancellationToken);
        return new TermExport(written, skipped, incomplete);
    }

    // The terms CSV can hold, trimmed by the import: none with |, none blank.
    private static List<Term> Kept(IReadOnlyList<Term> terms, List<string> lost)
    {
        var kept = new List<Term>();
        foreach (var term in terms)
        {
            if (term.Text.Contains('|', StringComparison.Ordinal))
            {
                Lose(lost, "terms with |");
            }
            else if (string.IsNullOrWhiteSpace(term.Text))
            {
                Lose(lost, "blank terms");
            }
            else
            {
                if (term.Text.Trim().Length != term.Text.Length)
                    Lose(lost, "the whitespace around terms");
                kept.Add(term);
            }
        }

        return kept;
    }

    private static void Lose(List<string> lost, string what)
    {
        if (!lost.Contains(what))
            lost.Add(what);
    }

    private static bool IsEncodable(string text)
    {
        try
        {
            StrictUtf8.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static string Row(IEnumerable<string> cells) =>
        string.Join(',', cells.Select(cell => '"' + (IsFormula(cell, minQuotes: 0) ? "'" + cell : cell).Replace("\"", "\"\"", StringComparison.Ordinal) + '"'));
}
