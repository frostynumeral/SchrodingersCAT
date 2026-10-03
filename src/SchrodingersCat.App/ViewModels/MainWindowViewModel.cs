using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

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
    }

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
            UnsavedChangesChoice.Save => await SaveToAsync(FilePath!),
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
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SaveToAsync(FilePath!);

    /// <summary>Saves to a new path, which becomes the open file. The original file is left as it was.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsAsync()
    {
        if (await dialogs.PickFileToSaveAsync(Path.GetFileName(FilePath)) is { } path && await SaveToAsync(path))
            FilePath = path;
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
