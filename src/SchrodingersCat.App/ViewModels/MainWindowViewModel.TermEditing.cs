using System.Text;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>The text selected when Ctrl+E is pressed: in the selected row's source, and in its editor (codes as spaces).</summary>
public sealed record TermSelection(string? Source, string? Target);

// Adding and editing terms (step 6 of docs/design/termbase.md): Ctrl+E and the entry dialog, editing and deleting from
// the terms list, importing and exporting, and re-checking only the units a change affects.
public sealed partial class MainWindowViewModel
{
    // How a write to an entry that may have gone meanwhile ended.
    private enum EntryWrite
    {
        Written,
        Unchanged,
        NotFound,
    }

    /// <summary>
    /// Ctrl+E (section 6): the entry dialog with the selected source and target text filled in, which offers the entries
    /// that have that source term already. The new term shows in the list and the source at once.
    /// </summary>
    [RelayCommand]
    private async Task AddTermAsync(TermSelection? selection)
    {
        if (TermRefusal() is { } refusal)
        {
            Report(refusal);
            return;
        }

        var termbase = Termbase!;
        var editor = TermEntryEditorViewModel.ForAdd(termbase.Index, termbase.Name, termbase.SourceLanguage, termbase.TargetLanguage,
            TermText.Clean(selection?.Source), TermText.Clean(selection?.Target));
        if (await dialogs.EditTermEntryAsync(editor) is not { } edit || !StillOpen(termbase, "nothing was added"))
            return;

        var (changed, changedBy) = (DateTimeOffset.UtcNow, Environment.UserName);
        var name = TermName(edit.Entry);
        if (edit.Id is not { } id)
        {
            var write = await WriteTermsAsync(termbase, $"Could not add the term to {termbase.Name}",
                t => t.Add(edit.Entry with { Changed = changed, ChangedBy = changedBy }));
            if (write is null)
                return;

            Notice = $"Added {name}{Translations(edit.Entry)} to {termbase.Name}.";
            ApplyTermWrite(termbase, write, [write.Result]);
            return;
        }

        // Merged in the queue, into the entry as it is then, so nothing written to it meanwhile is lost.
        var merge = await WriteTermsAsync(termbase, $"Could not add the term to {termbase.Name}", t =>
        {
            if (t.Index.Find(id) is not { } current)
                return EntryWrite.NotFound;

            var merged = TermEntryEdit.MergeInto(current, edit.Entry);
            if (merged == current)
                return EntryWrite.Unchanged;
            return t.Update(id, merged with { Changed = changed, ChangedBy = changedBy }) ? EntryWrite.Written : EntryWrite.NotFound;
        });
        if (merge is null)
            return;

        var entryName = merge.Before.Find(id) is { } before ? TermName(before) : name;
        Notice = merge.Result switch
        {
            EntryWrite.NotFound => $"The entry {entryName} is no longer in {termbase.Name}, so nothing was added.",
            EntryWrite.Unchanged => $"The entry {entryName} in {termbase.Name} has these terms already.",
            _ => $"Added {Added(merge.Before.Find(id)!, merge.After.Find(id)!)} to the entry {entryName} in {termbase.Name}.",
        };
        ApplyTermWrite(termbase, merge, [id]);
    }

    /// <summary>A terms list row's Edit: the entry in the entry dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTermEntry))]
    private async Task EditTermEntryAsync(TermHit? hit)
    {
        if (hit is null || Termbase is not { } termbase)
            return;
        if (termbase.Index.Find(hit.Id) is not { } current)
        {
            Notice = $"The entry {TermName(hit.Entry)} is no longer in {termbase.Name}.";
            return;
        }

        var editor = TermEntryEditorViewModel.ForEdit(hit.Id, current, termbase.Name, termbase.SourceLanguage, termbase.TargetLanguage);
        if (await dialogs.EditTermEntryAsync(editor) is not { } edit || TermEntryEdit.SameContent(edit.Entry, current)
            || !StillOpen(termbase, "the entry wasn't updated"))
            return;

        var (changed, changedBy) = (DateTimeOffset.UtcNow, Environment.UserName);
        var write = await WriteTermsAsync(termbase, $"Could not update the entry in {termbase.Name}",
            t => t.Update(hit.Id, edit.Entry with { Changed = changed, ChangedBy = changedBy }));
        if (write is null)
            return;

        Notice = write.Result
            ? $"Updated the entry {TermName(edit.Entry)} in {termbase.Name}."
            : $"The entry {TermName(current)} is no longer in {termbase.Name}, so it wasn't updated.";
        ApplyTermWrite(termbase, write, [hit.Id]);
    }

    /// <summary>A terms list row's Delete, asking first: there is no undo (section 6).</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTermEntry))]
    private async Task DeleteTermEntryAsync(TermHit? hit)
    {
        if (hit is null || Termbase is not { } termbase)
            return;

        var entry = termbase.Index.Find(hit.Id) ?? hit.Entry;
        var translations = string.Join(", ", TermTips.Ordered(entry).Select(t => t.Text));
        var pair = translations.Length == 0 ? $"“{Shorten(FirstTerm(entry))}”" : $"“{Shorten(FirstTerm(entry))}” → “{Shorten(translations)}”";
        if (!await dialogs.ConfirmAsync("Delete term entry", $"Delete {pair} from {termbase.Name}? This can't be undone.", "Delete")
            || !StillOpen(termbase, "nothing was deleted"))
            return;

        // Compacting is the termbase's to judge (only when a quarter is deleted), and cheap in the queue; if it fails, the
        // entry is gone all the same.
        var write = await WriteTermsAsync(termbase, $"Could not delete the entry from {termbase.Name}", t =>
        {
            var removed = t.Delete([hit.Id]);
            try
            {
                t.Compact();
                return (Removed: removed, CompactFailure: (string?)null);
            }
            catch (Exception ex) when (IsMemoryReportable(ex))
            {
                return (Removed: removed, CompactFailure: ex.Message);
            }
        });
        if (write is null)
            return;

        Notice = (write.Result.Removed == 0 ? $"The entry {TermName(entry)} is no longer in {termbase.Name}." : $"Deleted the entry {TermName(entry)} from {termbase.Name}.")
            + (write.Result.CompactFailure is { } failure ? $" Could not give back the space it took: {failure}" : "");
        ApplyTermWrite(termbase, write, [hit.Id]);
    }

    private bool CanChangeTermEntry(TermHit? hit) => hit is not null && Termbase is not null;

    private bool HasTermbase() => Termbase is not null;

    // Why Ctrl+E can't add a term now, as Ctrl+T says why it has nothing to insert; null if it can.
    private string? TermRefusal() => Termbase switch
    {
        null when LoadingTermbase is not null => "The termbase is still loading.",
        null => "No termbase is open. Create one with Termbase → New… or open one with Termbase → Open…",
        { } termbase when _document is not null && !TermbaseServes => $"The termbase “{termbase.Name}” doesn't serve this document's languages.",
        _ => null,
    };

    // In the status line of the selected segment, or without one, in the notice.
    private void Report(string message)
    {
        if (SelectedSegment is { } segment)
            segment.EditStatus = message;
        else
            Notice = message;
    }

    // While a dialog was open, the termbase may have been closed or replaced; then nothing is written to it.
    private bool StillOpen(OpenTermbase termbase, string consequence)
    {
        if (Termbase == termbase)
            return true;

        Notice = $"The termbase {termbase.Name} was closed, so {consequence}.";
        return false;
    }

    // A write in the termbase's queue (the UI doesn't wait for the commit); a failure goes to the banner. A write that
    // fails leaves the termbase as it was (ITermbase), so there's nothing to apply then.
    private async Task<TermWrite<T>?> WriteTermsAsync<T>(OpenTermbase termbase, string failure, Func<ITermbase, T> write)
    {
        try
        {
            return await termbase.WriteAsync(write);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"{failure}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// After a write to the termbase: the document takes the new snapshot, and only the units whose source has one of
    /// the changed entries' source terms, as they were before or are now, are checked again (section 5; found with
    /// <see cref="TermProbe"/>, step 3's decision). Each segment's cached hits follow without a lookup (the changed
    /// entries' hits replaced), so the marks, the list and Ctrl+T's choices show the change at once.
    /// </summary>
    /// <remarks>
    /// The units are checked with the new snapshot's term source, which takes over the previous one's records, not
    /// with the segments' cached hits: that would be a second term source to keep in step with the first, and the
    /// spike measured the two within 2 ms of each other (step 6's decision). If the document didn't have the snapshot
    /// the write started from (another write, or a re-check, came in between), it takes the termbase's latest and
    /// checks every unit again, as when the termbase is opened.
    /// </remarks>
    private void ApplyTermWrite<T>(OpenTermbase termbase, TermWrite<T> write, IReadOnlyCollection<TermEntryId> changed)
    {
        if (Termbase != termbase)
            return; // closed or replaced meanwhile: the document was checked again then

        if (_terms is not { } terms || !TermbaseServes || !ReferenceEquals(terms.Index, write.Before))
        {
            OnTermsChanged();
            return;
        }

        if (ReferenceEquals(write.Before, write.After))
            return; // nothing changed

        terms.Use(write.After, termbase);

        // Old and new versions alike: a unit that loses a renamed or deleted term loses its marks and issues.
        var probes = new List<TermProbe>();
        var found = new List<(TermEntryId Id, TermProbe Probe)>();
        foreach (var id in changed)
        {
            if (write.Before.Find(id) is { } old && !ReferenceEquals(old, write.After.Find(id)))
                probes.Add(TermProbe.Of(old));
            if (write.After.Find(id) is { } now)
            {
                var probe = TermProbe.Of(now);
                probes.Add(probe);
                found.Add((id, probe));
            }
        }

        var affected = new HashSet<UnitCheck>();
        foreach (var segment in Segments)
        {
            if (!segment.Check.IsToTranslate)
                continue;

            var source = segment.SourceQa;
            if (!probes.Any(p => p.IsIn(source)))
            {
                segment.RebaseTermHits(write.Before, write.After, changed, null);
                continue;
            }

            segment.RebaseTermHits(write.Before, write.After, changed, [.. found.Select(f => f.Probe.Find(source, f.Id)).OfType<TermHit>()]);
            affected.Add(segment.Check);
        }

        ShowTerms(SelectedSegment);

        // Units are small, but a term in most of the document (measured: 0.5–0.8 ms a unit, so 800 ms for 1,450 units)
        // would hold the UI thread: past the limit the whole document is checked again in the background instead, as
        // after an import. Not just the affected units there: a whole-document re-check still running with the
        // previous source would otherwise apply its results over theirs.
        if (affected.Count > InlineRecheckLimit)
        {
            TermCheck = RecheckTermsAsync(terms);
            TermCheck.Forget();
            return;
        }

        foreach (var check in affected)
            check.Run();
    }

    /// <summary>
    /// How many affected units a write re-checks on the UI thread; past it, the whole document is checked again in the
    /// background (<see cref="ApplyTermWrite"/>). About the 50 ms budget at the units' measured cost; tests change it.
    /// </summary>
    internal int InlineRecheckLimit { get; set; } = 64;

    // "“file”" for an entry: its first source term.
    private static string TermName(TermEntry entry) => $"“{Shorten(FirstTerm(entry))}”";

    private static string FirstTerm(TermEntry entry) =>
        entry.Source.Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim() ?? "";

    // " → “fájl”, “állomány”", or nothing for an entry without translations.
    private static string Translations(TermEntry entry) =>
        entry.Target.Count == 0 ? "" : " → " + string.Join(", ", entry.Target.Select(t => $"“{Shorten(t.Text)}”"));

    // The terms a merge added, and the note: "“állomány”, “mappa” and the note".
    private static string Added(TermEntry before, TermEntry after)
    {
        var terms = after.Source.Skip(before.Source.Count).Concat(after.Target.Skip(before.Target.Count)).Select(t => $"“{Shorten(t.Text)}”").ToList();
        if (after.Note != before.Note)
            terms.Add("the note");
        return terms.Count == 1 ? terms[0] : $"{string.Join(", ", terms[..^1])} and {terms[^1]}";
    }

    // ---------------------------------------------------------------- import and export (step 4's formats)

    /// <summary>
    /// Termbase → Import…: a TBX or CSV file into the open termbase, in one change; CSV through the dialog that asks for
    /// its encoding and columns. The termbase's date stamps entries without one (not its user: they are the client's
    /// terms). The whole document is checked again afterwards, as an import touches many units.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTermbase))]
    private async Task ImportTermsAsync()
    {
        var termbase = Termbase!;
        if (await dialogs.PickTermsToImportAsync() is not { } path)
            return;

        var name = Path.GetFileName(path);
        TermImport import;
        bool csv;
        try
        {
            csv = await Task.Run(() => IsCsv(path));
            if (csv)
            {
                var bytes = await Task.Run(async () =>
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    return await TermCsv.ReadBytesAsync(stream);
                });
                if (await dialogs.ChooseCsvImportAsync(new CsvImportViewModel(bytes, termbase.SourceLanguage, termbase.TargetLanguage, RunOffUiThread)) is not { } chosen)
                    return;
                import = chosen;
            }
            else
            {
                // Off the UI thread: a TBX file can be hundreds of megabytes.
                import = await Task.Run(async () =>
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    return await Tbx.ReadAsync(stream, termbase.SourceLanguage, termbase.TargetLanguage);
                });
            }
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not import {name}: {ex.Message}";
            return;
        }

        var report = ImportReport(import, csv ? "row" : "entry", csv ? "rows" : "entries");
        if (import.Entries.Count == 0)
        {
            Notice = $"Found no entries to import in {name}.{report}";
            return;
        }

        if (!StillOpen(termbase, "nothing was imported"))
            return;

        var now = DateTimeOffset.UtcNow;
        var entries = import.Entries.Select(e => e.Changed == default ? e with { Changed = now } : e).ToList();
        if (await WriteTermsAsync(termbase, $"Could not import {name}", t => t.AddRange(entries)) is null)
            return;

        Notice = $"Imported {Count(entries.Count, "entry", "entries")} from {name} into {termbase.Name}.{report}";
        if (Termbase == termbase)
            OnTermsChanged();
    }

    /// <summary>
    /// Termbase → Export…: the termbase as TBX 2, TBX 3 or CSV (the file type chosen), written atomically. What the
    /// termbase holds now is exported, without waiting for queued writes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTermbase))]
    private async Task ExportTermsAsync()
    {
        var termbase = Termbase!;
        if (await dialogs.PickTermsToExportAsync($"{termbase.Name}.tbx") is not { } target)
            return;

        var name = Path.GetFileName(target.Path);
        var entries = termbase.Index.Entries().Select(e => e.Entry).ToList();
        var (source, targetLanguage) = (termbase.SourceLanguage, termbase.TargetLanguage);
        TermExport export;
        try
        {
            export = await Task.Run(() => WriteAtomicallyAsync(target.Path, stream => target.Format switch
            {
                TermExportFormat.Csv => TermCsv.WriteAsync(stream, entries, source, targetLanguage),
                TermExportFormat.Tbx3 => Tbx.WriteAsync(stream, entries, source, targetLanguage, TbxVersion.Tbx3),
                _ => Tbx.WriteAsync(stream, entries, source, targetLanguage, TbxVersion.Tbx2),
            }));
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not export to {name}: {ex.Message}";
            return;
        }

        var incomplete = export.Incomplete.Count == 0
            ? ""
            : $" Exported without what CSV can't hold: {Count(export.Incomplete.Count, "entry", "entries")} ({string.Join("; ", export.Incomplete.Take(3))}{(export.Incomplete.Count > 3 ? "; …" : "")}).";
        Notice = $"Exported {Count(export.Written, "entry", "entries")} from {termbase.Name} to {name}.{Skipped(export.Skipped, "entry", "entries")}{incomplete}";
    }

    // " Skipped 2 entries: …. 3 notices: …. Dropped: subjectField ×12, other languages ×4."
    private static string ImportReport(TermImport import, string one, string many)
    {
        var report = new StringBuilder(Skipped(import.Skipped, one, many));
        if (import.Notices.Count > 0)
        {
            report.Append(System.Globalization.CultureInfo.InvariantCulture,
                $" {Count(import.Notices.Count, "notice", "notices")}: {string.Join("; ", import.Notices.Take(3))}{(import.Notices.Count > 3 ? "; …" : "")}");
        }

        if (import.Dropped.Count > 0)
            report.Append(" Dropped: ").AppendJoin(", ", import.Dropped.Select(d => $"{d.Key} ×{d.Value}")).Append('.');
        return report.ToString();
    }

    // CSV or TBX: by the extension, else by the first character that isn't a BOM or whitespace (TBX starts with '<').
    private static bool IsCsv(string path)
    {
        switch (Path.GetExtension(path).ToUpperInvariant())
        {
            case ".TBX" or ".XML":
                return false;
            case ".CSV" or ".TSV" or ".TXT":
                return true;
        }

        var start = new byte[1024];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        var bytes = start.AsSpan(0, read);
        var text = bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? Encoding.Unicode.GetString(bytes)
            : bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
        return !text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('<');
    }
}

/// <summary>Text selected for a term: trimmed, whitespace runs (line breaks, codes turned into spaces) as one space.</summary>
public static class TermText
{
    public static string Clean(string? text) =>
        text is null ? "" : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
