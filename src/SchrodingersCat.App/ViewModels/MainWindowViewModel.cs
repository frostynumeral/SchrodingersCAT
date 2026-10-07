using System.Collections.ObjectModel;
using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <param name="settings">Where the open TM is remembered between sessions; null remembers nothing.</param>
public sealed partial class MainWindowViewModel(IXliffSerializer serializer, IDialogService dialogs, ISettingsStore? settings = null) : ObservableObject
{
    private TranslationDocument? _document;

    public ObservableCollection<SegmentViewModel> Segments { get; } = [];

    /// <summary>The segment being worked on: its target is shown in the editor, the others read-only.</summary>
    [ObservableProperty]
    public partial SegmentViewModel? SelectedSegment { get; set; }

    /// <summary>The open file; saving writes back to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    public partial string? FilePath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; private set; }

    /// <summary>The last load or save failure, shown to the user until dismissed or the next success.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    partial void OnSelectedSegmentChanged(SegmentViewModel? oldValue, SegmentViewModel? newValue)
    {
        oldValue?.IsActive = false;
        newValue?.IsActive = true;
        Notice = null;
        MatchLookup = LookUpMatchesAsync();
    }

    /// <summary>
    /// The outcome of the last action that has no segment to report it in: a confirmed segment that wasn't stored, an
    /// import or export. Cleared when the selection changes.
    /// </summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>The column headers, with the document's languages.</summary>
    public string SourceHeader => _document is null ? "Source" : $"Source ({_document.SourceLanguage})";

    public string TargetHeader => _document?.TargetLanguage is { } language ? $"Target ({language})" : "Target";

    public string Title => FilePath is null
        ? "SchrödingersCAT"
        : $"{Path.GetFileName(FilePath)}{(IsDirty ? " •" : "")} — SchrödingersCAT";

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (!await ResolveUnsavedChangesAsync())
            return;

        if (await dialogs.PickFileToOpenAsync() is { } path)
            await LoadAsync(path);
    }

    /// <summary>
    /// Called when the window is about to close. Returns whether it may: true if there is nothing to lose, or the
    /// user saved (successfully) or chose not to; false if they cancelled or the save failed.
    /// </summary>
    public Task<bool> ConfirmCloseAsync() => ResolveUnsavedChangesAsync();

    // Unsaved edits are never dropped without asking. A failed save counts as Cancel, so nothing is lost either way.
    private async Task<bool> ResolveUnsavedChangesAsync()
    {
        if (!IsDirty)
            return true;

        return await dialogs.AskToSaveChangesAsync(Path.GetFileName(FilePath!)) switch
        {
            UnsavedChangesChoice.Save => await ReadyToSaveAsync() && await SaveToAsync(FilePath!),
            UnsavedChangesChoice.DontSave => true,
            _ => false,
        };
    }

    /// <summary>Opens a file. On failure the error is shown and the current document stays open.</summary>
    public async Task LoadAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        TranslationDocument document;
        try
        {
            document = await serializer.ReadFileAsync(path);
        }
        catch (Exception ex) when (IsReportable(ex))
        {
            ErrorMessage = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        _document = document;
        SelectedSegment = null;
        Segments.Clear();
        foreach (var unit in document.Units)
        {
            var check = new UnitCheck(unit);
            foreach (var segment in unit.TranslatableSegments)
                Segments.Add(new SegmentViewModel(check, segment, () => IsDirty = true));
        }

        FilePath = path;
        IsDirty = false;
        ErrorMessage = null;
        OnPropertyChanged(nameof(SourceHeader));
        OnPropertyChanged(nameof(TargetHeader));
        OnPropertyChanged(nameof(MemoryLabel));
        NewTranslationMemoryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (await ReadyToSaveAsync())
            await SaveToAsync(FilePath!);
    }

    /// <summary>Saves to a new path, which becomes the open file. The original file is left as it was.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsAsync()
    {
        if (await ReadyToSaveAsync() && await dialogs.PickFileToSaveAsync(Path.GetFileName(FilePath)) is { } path && await SaveToAsync(path))
            FilePath = path;
    }

    // What a save needs from the translator first; false if they cancelled.
    private async Task<bool> ReadyToSaveAsync() => await ConfirmCodeErrorsAsync() && await EnsureTargetLanguageAsync();

    // XLIFF requires trgLang once there are targets, and a file may come without one: ask when it's needed (and before
    // creating a TM, which needs it, if <paramref name="required"/>).
    private async Task<bool> EnsureTargetLanguageAsync(bool required = false)
    {
        var document = _document!;
        if (document.TargetLanguage is not null || (!required && !document.Units.Any(u => u.Segments.Any(s => s.Target is not null))))
            return true;

        var tag = await dialogs.AskTargetLanguageAsync(document.SourceLanguage, t => LanguageTag.Problem(t, document.SourceLanguage));
        if (tag is null || LanguageTag.Problem(tag, document.SourceLanguage) is not null)
            return false;

        document.TargetLanguage = tag.Trim();
        OnPropertyChanged(nameof(TargetHeader));
        OnPropertyChanged(nameof(MemoryLabel));
        return true;
    }

    // Decision 2 of docs/design/inline-code-editing.md: warn, and let the translator save unfinished work anyway.
    // (The writer still refuses what XLIFF can't hold, such as unpaired codes; that failure shows in the banner.)
    private async Task<bool> ConfirmCodeErrorsAsync()
    {
        var withErrors = Segments.Where(s => s.HasErrors).Select(s => s.Label).ToList();
        if (withErrors.Count == 0)
            return true;

        var names = string.Join(", ", withErrors.Take(3)) + (withErrors.Count > 3 ? ", …" : "");
        var message = withErrors.Count == 1
            ? $"Segment {names} has code errors. Save anyway?"
            : $"{withErrors.Count} segments have code errors ({names}). Save anyway?";
        return await dialogs.ConfirmAsync("Code errors", message, "Save anyway");
    }

    private async Task<bool> SaveToAsync(string path)
    {
        try
        {
            // Atomic: writes <path>.tmp and moves it over the file, so a failed save leaves the original intact.
            await serializer.WriteFileAsync(_document!, path);
        }
        catch (Exception ex) when (IsReportable(ex))
        {
            ErrorMessage = $"Could not save {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }

        IsDirty = false;
        ErrorMessage = null;
        return true;
    }

    private bool CanSave() => _document is not null && FilePath is not null;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // ---------------------------------------------------------------- keyboard workflow (design section 4)

    /// <summary>Ctrl+Down: the next segment (the first if none is selected).</summary>
    [RelayCommand]
    private void NextSegment() => Move(1);

    /// <summary>Ctrl+Up: the previous segment (the last if none is selected).</summary>
    [RelayCommand]
    private void PreviousSegment() => Move(-1);

    /// <summary>Ctrl+Shift+Down: the next segment with code errors or warnings (info is normal in translation).</summary>
    [RelayCommand]
    private void NextSegmentWithIssues()
    {
        var start = SelectedSegment is null ? 0 : Segments.IndexOf(SelectedSegment) + 1;
        var next = Segments.Skip(start).FirstOrDefault(s => s.HasErrors || s.HasWarnings);
        if (next is not null)
            SelectedSegment = next;
        else
            SelectedSegment?.EditStatus = "No segments with code errors or warnings below this one.";
    }

    /// <summary>
    /// Ctrl+Enter: confirms the selected segment, stores it in the open TM (step 5 of the TM design), and moves to the
    /// next one. The store is queued before the move queues the next lookup, so a repetition below finds it: nothing
    /// may be awaited before <see cref="Store"/>.
    /// </summary>
    [RelayCommand]
    private void ConfirmSegment()
    {
        if (SelectedSegment is not { } segment || !segment.Confirm())
            return;

        var notice = Store(segment);
        Move(1);
        Notice = notice;
    }

    /// <summary>Ctrl+Insert: replaces the selected segment's target with its source, asking first if there is one.</summary>
    [RelayCommand]
    private async Task CopySourceAsync()
    {
        if (SelectedSegment is not { } segment)
            return;

        if (segment.HasTarget && !await dialogs.ConfirmAsync(
                "Copy source", "Replace the translation with the source text and codes? You can undo this with Ctrl+Z.", "Replace"))
            return;

        segment.CopySource();
    }

    // ---------------------------------------------------------------- matches (translation memory, design section 6)

    // Cancels the running lookup when the selection moves on. (The lookup owns and disposes its token source.)
    private Action? _cancelLookup;

    /// <summary>The selected segment's matches, best first: the open TM's and the file's, at most nine (Ctrl+1 … Ctrl+9).</summary>
    public ObservableCollection<MatchViewModel> Matches { get; } = [];

    /// <summary>
    /// The open TM: matches are looked up in it, and Ctrl+Enter stores in it. Null if none is open (then only the
    /// file's matches are shown). Changed through the Translation memory menu or <see cref="UseTranslationMemoryAsync"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchesStatus))]
    [NotifyPropertyChangedFor(nameof(MemoryLabel))]
    [NotifyCanExecuteChangedFor(nameof(CloseTranslationMemoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportTmxCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportTmxCommand))]
    public partial OpenTranslationMemory? TranslationMemory { get; private set; }

    partial void OnTranslationMemoryChanged(OpenTranslationMemory? value) => MatchLookup = LookUpMatchesAsync();

    /// <summary>The open TM and its languages for the pane's header, and why it can't be used for this document.</summary>
    public string? MemoryLabel
    {
        get
        {
            if (TranslationMemory is not { } memory)
                return null;

            var label = $"{memory.Name} ({memory.SourceLanguage} → {memory.TargetLanguage})";
            if (_document is not { } document)
                return label;

            var documentLanguages = $"{document.SourceLanguage} → {document.TargetLanguage ?? "?"}";
            return MemoryPenalty switch
            {
                null => $"{label} doesn't serve this document ({documentLanguages}).",
                > 0 and var penalty => $"{label}: matches lose {penalty} point for this document ({documentLanguages}).",
                _ => label,
            };
        }
    }

    // What every TM match loses for this document (decision 3), or null if the TM doesn't serve it: then nothing is
    // looked up or stored. Without a document, nothing is either.
    private int? MemoryPenalty => TranslationMemory is { } memory && _document is { } document
        ? TmLanguages.Penalty(memory.SourceLanguage, memory.TargetLanguage, document.SourceLanguage, document.TargetLanguage)
        : null;

    /// <summary>The latest store in the TM; tests await it.</summary>
    internal Task StoreTask { get; private set; } = Task.CompletedTask;

    /// <summary>A TM lookup for the selected segment is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchesStatus))]
    public partial bool IsLookingUp { get; private set; }

    /// <summary>What the matches pane says instead of matches; null when it has some.</summary>
    public string? MatchesStatus => (SelectedSegment, Matches.Count, IsLookingUp, TranslationMemory) switch
    {
        (null, _, _, _) => "Select a segment to see its matches.",
        (_, > 0, _, _) => null,
        (_, _, true, _) => "Looking up matches…",
        (_, _, _, null) => "No matches. No translation memory is open.",
        _ => "No matches.",
    };

    /// <summary>The latest matches lookup; tests await it.</summary>
    internal Task MatchLookup { get; private set; } = Task.CompletedTask;

    /// <summary>Ctrl+1 … Ctrl+9: inserts the selected segment's nth match into its target.</summary>
    [RelayCommand]
    private void InsertMatch(int number)
    {
        if (SelectedSegment is not { } segment)
            return;

        if (number >= 1 && number <= Matches.Count)
            segment.InsertMatch(Matches[number - 1]);
        else
            segment.EditStatus = Matches.Count == 0 ? "There are no matches to insert." : $"There is no match {number}.";
    }

    // The file's matches show straight away; the TM's follow from a background lookup, unless the selection has moved
    // on by then. Once the matches are complete, an exact one goes into an empty target (decision 2).
    private async Task LookUpMatchesAsync()
    {
        _cancelLookup?.Invoke();
        _cancelLookup = null;
        IsLookingUp = false;

        var segment = SelectedSegment;
        ShowMatches(segment, []);
        if (segment is null)
            return;
        if (TranslationMemory is not { } memory || MemoryPenalty is not { } penalty)
        {
            AutoInsert(segment);
            return;
        }

        using var lookup = new CancellationTokenSource();
        Action cancel = lookup.Cancel;
        _cancelLookup = cancel;
        IsLookingUp = true;
        OnPropertyChanged(nameof(MatchesStatus));
        try
        {
            var (previous, next) = Neighbours(segment);
            var found = await memory.LookupAsync(segment.Source, previous, next, penalty, lookup.Token);
            if (lookup.IsCancellationRequested)
                return;
            ShowMatches(segment, found);
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is TmFormatException or DbException or IOException)
        {
            // A damaged TM is shown like a damaged file; the translator can go on with the file's matches.
            ErrorMessage = $"Could not look up matches in {memory.Name}: {ex.Message}";
        }
        finally
        {
            if (_cancelLookup == cancel)
            {
                _cancelLookup = null;
                IsLookingUp = false;
            }
        }

        AutoInsert(segment);
    }

    // Only a TM's match: a file's match may be machine translation scored 100 (the XLIFF reader doesn't tell).
    private void AutoInsert(SegmentViewModel segment)
    {
        if (ReferenceEquals(SelectedSegment, segment) && segment.CanAutoInsert && Matches is [{ IsExact: true, IsFromFile: false } best, ..])
            segment.InsertMatch(best, automatic: true);
    }

    // The neighbouring segments' plain text: the context of a lookup, and of what Ctrl+Enter stores, so 101% matches
    // are found again.
    private (string? Previous, string? Next) Neighbours(SegmentViewModel segment)
    {
        var index = Segments.IndexOf(segment);
        var previous = index > 0 ? Segments[index - 1].Source.ToPlainText() : null;
        var next = index >= 0 && index < Segments.Count - 1 ? Segments[index + 1].Source.ToPlainText() : null;
        return (previous, next);
    }

    // Queues the confirmed translation for the open TM, replacing what the segment stored before; returns a notice if
    // it isn't stored. StoredTarget changes at once: the queue keeps the order, so a quick second confirmation
    // replaces this one.
    private string? Store(SegmentViewModel segment)
    {
        if (TranslationMemory is not { } memory || MemoryPenalty is null || segment.Target is not { IsEmpty: false } target)
            return null;
        if (segment.HasErrors)
            return $"{segment.Label} was confirmed but not stored in {memory.Name}: its target has code errors.";

        // Storing says the translation is in the TM's target language; a file without trgLang takes it, so a later
        // save can't declare another language for what the TM now holds.
        if (_document!.TargetLanguage is null)
        {
            _document.TargetLanguage = memory.TargetLanguage;
            OnPropertyChanged(nameof(TargetHeader));
            OnPropertyChanged(nameof(MemoryLabel));
        }

        var (previous, next) = Neighbours(segment);
        var entry = new TmEntry(segment.Source, target)
        {
            PreviousSource = previous,
            NextSource = next,
            Document = FilePath is null ? null : Path.GetFileName(FilePath),
            Changed = DateTimeOffset.UtcNow,
            ChangedBy = Environment.UserName,
        };
        var replacing = segment.StoredTarget;
        segment.StoredTarget = target;
        StoreTask = StoreAsync(memory, segment, entry, replacing);
        return null;
    }

    private async Task StoreAsync(OpenTranslationMemory memory, SegmentViewModel segment, TmEntry entry, InlineContent? replacing)
    {
        try
        {
            await memory.StoreAsync(entry, replacing);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not store {segment.Label} in {memory.Name}: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- the Translation memory menu (TM design, step 5)

    /// <summary>
    /// Makes <paramref name="memory"/> the open TM (null closes it) and closes the previous one once its queued work is
    /// done. <paramref name="remember"/>: open it again next time; false when the app shuts down, so it's remembered.
    /// </summary>
    public async Task UseTranslationMemoryAsync(OpenTranslationMemory? memory, bool remember = true)
    {
        var previous = TranslationMemory;
        TranslationMemory = memory;
        if (remember)
            Remember(memory?.Path);
        if (previous is not null && previous != memory)
            await previous.DisposeAsync();
    }

    /// <summary>
    /// Called at startup: opens the TM that was open last time. If it can't be opened, that's reported; a damaged one is
    /// forgotten, one that's missing isn't (it may be on a drive that isn't plugged in).
    /// </summary>
    public void RestoreTranslationMemory()
    {
        if (TranslationMemory is not null || settings?.Load().TranslationMemoryPath is not { } path)
            return;

        try
        {
            TranslationMemory = OpenTranslationMemory.Open(path);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not open the translation memory {Path.GetFileName(path)}: {ex.Message}";
            if (ex is TmFormatException)
                Remember(null);
        }
    }

    /// <summary>Called when the window closes: finishes the queued TM work and closes the TM, still remembering it.</summary>
    public Task ShutDownAsync() => UseTranslationMemoryAsync(null, remember: false);

    /// <summary>Creates a TM for the open document's languages, asking for the target language first if the file has none.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task NewTranslationMemoryAsync()
    {
        var document = _document!;
        if (!await EnsureTargetLanguageAsync(required: true)
            || await dialogs.PickTranslationMemoryToCreateAsync($"{document.SourceLanguage}-{document.TargetLanguage}.sctm") is not { } path)
            return;

        // Never replace a TM: years of work can be in one.
        var name = Path.GetFileName(path);
        if (File.Exists(path))
        {
            ErrorMessage = $"{name} already exists. Open it with Translation memory → Open…, or choose another name.";
            return;
        }

        OpenTranslationMemory memory;
        try
        {
            memory = OpenTranslationMemory.Create(path, document.SourceLanguage, document.TargetLanguage!);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not create {name}: {ex.Message}";
            return;
        }

        ErrorMessage = null;
        await UseTranslationMemoryAsync(memory);
    }

    [RelayCommand]
    private async Task OpenTranslationMemoryAsync()
    {
        if (await dialogs.PickTranslationMemoryToOpenAsync() is not { } path)
            return;

        OpenTranslationMemory memory;
        try
        {
            memory = OpenTranslationMemory.Open(path);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        ErrorMessage = null;
        await UseTranslationMemoryAsync(memory);
    }

    [RelayCommand(CanExecute = nameof(HasTranslationMemory))]
    private Task CloseTranslationMemoryAsync() => UseTranslationMemoryAsync(null);

    /// <summary>Imports a TMX file into the open TM, in its languages and their regional variants; skipped units are reported.</summary>
    [RelayCommand(CanExecute = nameof(HasTranslationMemory))]
    private async Task ImportTmxAsync()
    {
        var memory = TranslationMemory!;
        if (await dialogs.PickTmxToImportAsync() is not { } path)
            return;

        var name = Path.GetFileName(path);
        TmxImport import;
        try
        {
            // Off the UI thread: a TMX file can be hundreds of megabytes.
            import = await Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                return await Tmx.ReadAsync(stream, memory.SourceLanguage, memory.TargetLanguage);
            });
            await memory.ImportAsync(import.Entries);
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not import {name}: {ex.Message}";
            return;
        }

        Notice = $"Imported {Count(import.Entries.Count, "entry", "entries")} from {name} into {memory.Name}.{Skipped(import.Skipped, "unit")}";
        MatchLookup = LookUpMatchesAsync();
    }

    /// <summary>Exports the open TM to a TMX file, written atomically; entries XML can't hold are skipped and reported.</summary>
    [RelayCommand(CanExecute = nameof(HasTranslationMemory))]
    private async Task ExportTmxAsync()
    {
        var memory = TranslationMemory!;
        if (await dialogs.PickTmxToExportAsync(Path.ChangeExtension(memory.Name, ".tmx")) is not { } path)
            return;

        var name = Path.GetFileName(path);
        TmxExport export;
        try
        {
            var entries = await memory.EntriesAsync();
            export = await Task.Run(() => WriteTmxAsync(path, entries, memory.SourceLanguage, memory.TargetLanguage));
        }
        catch (Exception ex) when (IsMemoryReportable(ex))
        {
            ErrorMessage = $"Could not export to {name}: {ex.Message}";
            return;
        }

        Notice = $"Exported {Count(export.Written, "entry", "entries")} from {memory.Name} to {name}.{Skipped(export.Skipped, "entry")}";
    }

    // Like XliffSerializerExtensions.WriteFileAsync: a failed export leaves an existing file as it was.
    private static async Task<TmxExport> WriteTmxAsync(string path, IReadOnlyList<TmEntry> entries, string sourceLanguage, string targetLanguage)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = fullPath + ".tmp";
        try
        {
            TmxExport export;
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                export = await Tmx.WriteAsync(stream, entries, sourceLanguage, targetLanguage);

            File.Move(tempPath, fullPath, overwrite: true);
            return export;
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    private bool HasDocument() => _document is not null;

    private bool HasTranslationMemory() => TranslationMemory is not null;

    private void Remember(string? path)
    {
        if (settings is null)
            return;

        try
        {
            settings.Save(new AppSettings(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not remember the translation memory for next time: {ex.Message}";
        }
    }

    private static string Count(int count, string one, string many) =>
        $"{count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {(count == 1 ? one : many)}";

    // " Skipped 2 units: a; b." with the first three reasons.
    private static string Skipped(IReadOnlyList<string> skipped, string what) => skipped.Count == 0
        ? ""
        : $" Skipped {Count(skipped.Count, what, what + "s")}: {string.Join("; ", skipped.Take(3))}{(skipped.Count > 3 ? "; …" : "")}";

    // What a TM, a TMX file or the file system report for bad files or unavailable paths; anything else is a bug.
    private static bool IsMemoryReportable(Exception ex) =>
        ex is TmFormatException or DbException or IOException or UnauthorizedAccessException or ArgumentException
            or ObjectDisposedException;

    private void ShowMatches(SegmentViewModel? segment, IReadOnlyList<TmMatch> fromMemory)
    {
        Matches.Clear();
        if (segment is not null)
        {
            // Best first; among equal scores the TM's first (OrderByDescending keeps their order).
            var all = fromMemory.Select(m => (Match: m, FromFile: false))
                .Concat(segment.FileMatches.Select(m => (Match: m, FromFile: true)))
                .OrderByDescending(m => m.Match.Score)
                .Take(9)
                .ToList();
            for (var i = 0; i < all.Count; i++)
                Matches.Add(new MatchViewModel(i + 1, all[i].Match, all[i].FromFile, segment));
        }

        OnPropertyChanged(nameof(MatchesStatus));
    }

    private void Move(int step)
    {
        if (Segments.Count == 0)
            return;

        var index = SelectedSegment is null ? (step > 0 ? 0 : Segments.Count - 1) : Segments.IndexOf(SelectedSegment) + step;
        if (index >= 0 && index < Segments.Count)
            SelectedSegment = Segments[index];
    }

    // Everything the serializer and the file system document for bad input, bad models or unavailable files.
    // Anything else is a bug and should not be dressed up as a user-facing message.
    private static bool IsReportable(Exception ex) =>
        ex is XliffFormatException or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException or ArgumentException;
}
