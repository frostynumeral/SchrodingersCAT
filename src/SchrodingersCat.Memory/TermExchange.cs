namespace SchrodingersCat.Memory;

/// <summary>
/// What a TBX or CSV import found (section 4 of <c>docs/design/termbase.md</c>): the entries for the termbase's
/// language pair, to store with one <see cref="ITermbase.AddRange"/>, and what it couldn't use.
/// </summary>
/// <param name="Skipped">Entries (or rows) left out, and why: <c>"Entry 3: no en term."</c></param>
/// <param name="Notices">What was imported, but not as it stood: an unknown status, a date that can't be read.</param>
/// <param name="Dropped">The data categories the termbase doesn't keep (other languages, subject fields, …), and how often each was dropped.</param>
public sealed record TermImport(
    IReadOnlyList<TermEntry> Entries,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Notices,
    IReadOnlyDictionary<string, int> Dropped);

/// <summary>What a TBX or CSV export wrote, and what it couldn't.</summary>
/// <param name="Skipped">Entries not written at all, and why.</param>
/// <param name="Incomplete">Entries written without something the format can't hold (CSV only), one line each.</param>
public sealed record TermExport(int Written, IReadOnlyList<string> Skipped, IReadOnlyList<string> Incomplete);

/// <summary>The status values TBX and CSV use (the table in section 4 of the design).</summary>
internal static class TermStatusNames
{
    private static readonly Dictionary<string, TermStatus> Tbx = new(StringComparer.OrdinalIgnoreCase)
    {
        // administrativeStatus
        ["preferredTerm-admn-sts"] = TermStatus.Preferred,
        ["admittedTerm-admn-sts"] = TermStatus.Admitted,
        ["deprecatedTerm-admn-sts"] = TermStatus.Forbidden,
        ["supersededTerm-admn-sts"] = TermStatus.Forbidden,

        // normativeAuthorization
        ["preferredTerm"] = TermStatus.Preferred,
        ["standardizedTerm"] = TermStatus.Preferred,
        ["legalTerm"] = TermStatus.Preferred,
        ["regulatedTerm"] = TermStatus.Preferred,
        ["admittedTerm"] = TermStatus.Admitted,
        ["deprecatedTerm"] = TermStatus.Forbidden,
        ["supersededTerm"] = TermStatus.Forbidden,
    };

    // CSV takes TBX's values too, as a spreadsheet exported from a TBX file may have them.
    private static readonly Dictionary<string, TermStatus> Csv = new(Tbx, StringComparer.OrdinalIgnoreCase)
    {
        ["preferred"] = TermStatus.Preferred,
        ["admitted"] = TermStatus.Admitted,
        ["forbidden"] = TermStatus.Forbidden,
        ["deprecated"] = TermStatus.Forbidden,
        ["superseded"] = TermStatus.Forbidden,
    };

    /// <summary>The status a TBX value names, ignoring case and surrounding whitespace; null if it names none.</summary>
    public static TermStatus? ParseTbx(string value) => Tbx.TryGetValue(value.Trim(), out var status) ? status : null;

    /// <summary>The status a CSV cell names: CSV's words or TBX's values, ignoring case and surrounding whitespace; null if it names none.</summary>
    public static TermStatus? ParseCsv(string value) => Csv.TryGetValue(value.Trim(), out var status) ? status : null;

    /// <summary>TBX-Basic's <c>administrativeStatus</c> value; null for <see cref="TermStatus.Unspecified"/>, which writes none.</summary>
    public static string? AdministrativeStatus(TermStatus status) => status switch
    {
        TermStatus.Preferred => "preferredTerm-admn-sts",
        TermStatus.Admitted => "admittedTerm-admn-sts",
        TermStatus.Forbidden => "deprecatedTerm-admn-sts",
        _ => null,
    };

    /// <summary>The word CSV writes; empty for <see cref="TermStatus.Unspecified"/>.</summary>
    public static string Word(TermStatus status) => status switch
    {
        TermStatus.Preferred => "preferred",
        TermStatus.Admitted => "admitted",
        TermStatus.Forbidden => "forbidden",
        _ => "",
    };
}
