using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.ViewModels;

public sealed partial class MainWindowViewModel(IXliffSerializer serializer, IFileDialogService dialogs) : ObservableObject
{
    private TranslationDocument? _document;

    public ObservableCollection<SegmentViewModel> Segments { get; } = [];

    /// <summary>The open file; saving writes back to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? FilePath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsDirty { get; private set; }

    /// <summary>The last load or save failure, shown to the user until dismissed or the next success.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public string Title => FilePath is null
        ? "SchrödingersCAT"
        : $"{Path.GetFileName(FilePath)}{(IsDirty ? " •" : "")} — SchrödingersCAT";

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await dialogs.PickFileToOpenAsync() is { } path)
            await LoadAsync(path);
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
        Segments.Clear();
        foreach (var unit in document.Units)
        {
            foreach (var segment in unit.TranslatableSegments)
                Segments.Add(new SegmentViewModel(unit, segment, () => IsDirty = true));
        }

        FilePath = path;
        IsDirty = false;
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        try
        {
            // Atomic: writes <path>.tmp and moves it over the file, so a failed save leaves the original intact.
            await serializer.WriteFileAsync(_document!, FilePath!);
        }
        catch (Exception ex) when (IsReportable(ex))
        {
            ErrorMessage = $"Could not save {Path.GetFileName(FilePath)}: {ex.Message}";
            return;
        }

        IsDirty = false;
        ErrorMessage = null;
    }

    private bool CanSave() => _document is not null && FilePath is not null;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // Everything the serializer and the file system document for bad input, bad models or unavailable files.
    // Anything else is a bug and should not be dressed up as a user-facing message.
    private static bool IsReportable(Exception ex) =>
        ex is XliffFormatException or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException or ArgumentException;
}
