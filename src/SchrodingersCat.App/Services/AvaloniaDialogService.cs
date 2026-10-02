using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SchrodingersCat.App.Services;

/// <summary><see cref="IFileDialogService"/> on top of Avalonia's storage provider for a window.</summary>
public sealed class StorageFileDialogService(TopLevel topLevel) : IFileDialogService
{
    private static readonly FilePickerFileType Xliff = new("XLIFF files") { Patterns = ["*.xlf", "*.xliff"] };

    public async Task<string?> PickFileToOpenAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open XLIFF file",
            AllowMultiple = false,
            FileTypeFilter = [Xliff, FilePickerFileTypes.All],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
