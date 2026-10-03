using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SchrodingersCat.App.Views;

namespace SchrodingersCat.App.Services;

/// <summary><see cref="IDialogService"/> on top of Avalonia's storage provider and dialogs, owned by a window.</summary>
public sealed class AvaloniaDialogService(Window owner) : IDialogService
{
    private static readonly FilePickerFileType Xliff = new("XLIFF files") { Patterns = ["*.xlf", "*.xliff"] };

    public async Task<string?> PickFileToOpenAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open XLIFF file",
            AllowMultiple = false,
            FileTypeFilter = [Xliff, FilePickerFileTypes.All],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickFileToSaveAsync(string? suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save XLIFF file as",
            SuggestedFileName = suggestedName,
            DefaultExtension = "xlf",
            FileTypeChoices = [Xliff],
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string fileName) =>
        new UnsavedChangesDialog(fileName).ShowDialog<UnsavedChangesChoice>(owner);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        new ConfirmDialog(title, message, confirmLabel).ShowDialog<bool>(owner);
}
