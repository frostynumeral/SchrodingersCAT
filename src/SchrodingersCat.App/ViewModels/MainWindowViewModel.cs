using System.Collections.ObjectModel;
using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

public sealed partial class MainWindowViewModel(IXliffSerializer serializer, IDialogService dialogs) : ObservableObject
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
        MatchLookup = LookUpMatchesAsync();
    }

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

    // XLIFF requires trgLang once there are targets, and a file may come without one: ask when it's needed.
    private async Task<bool> EnsureTargetLanguageAsync()
    {
        var document = _document!;
        if (document.TargetLanguage is not null || !document.Units.Any(u => u.Segments.Any(s => s.Target is not null)))
            return true;

        var tag = await dialogs.AskTargetLanguageAsync(document.SourceLanguage, t => LanguageTag.Problem(t, document.SourceLanguage));
        if (tag is null || LanguageTag.Problem(tag, document.SourceLanguage) is not null)
            return false;

        document.TargetLanguage = tag.Trim();
        OnPropertyChanged(nameof(TargetHeader));
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

    /// <summary>Ctrl+Enter: confirms the selected segment and moves to the next one.</summary>
    [RelayCommand]
    private void ConfirmSegment()
    {
        if (SelectedSegment?.Confirm() == true)
            Move(1);
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

    /// <summary>The TM matches are looked up in; null if none is open (then only the file's matches are shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchesStatus))]
    public partial OpenTranslationMemory? TranslationMemory { get; set; }

    partial void OnTranslationMemoryChanged(OpenTranslationMemory? value) => MatchLookup = LookUpMatchesAsync();

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
    // on by then.
    private async Task LookUpMatchesAsync()
    {
        _cancelLookup?.Invoke();
        _cancelLookup = null;
        IsLookingUp = false;

        var segment = SelectedSegment;
        ShowMatches(segment, []);
        if (segment is null || TranslationMemory is not { } memory)
            return;

        using var lookup = new CancellationTokenSource();
        Action cancel = lookup.Cancel;
        _cancelLookup = cancel;
        IsLookingUp = true;
        OnPropertyChanged(nameof(MatchesStatus));
        try
        {
            var index = Segments.IndexOf(segment);
            var previous = index > 0 ? Segments[index - 1].Source.ToPlainText() : null;
            var next = index >= 0 && index < Segments.Count - 1 ? Segments[index + 1].Source.ToPlainText() : null;
            var found = await memory.LookupAsync(segment.Source, previous, next, lookup.Token);
            if (!lookup.IsCancellationRequested)
                ShowMatches(segment, found);
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is TmFormatException or DbException or IOException)
        {
            // A damaged TM is shown like a damaged file; the translator can go on without matches.
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
    }

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
