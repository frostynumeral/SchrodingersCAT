using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Services;

/// <summary><see cref="IDialogService"/> on top of Avalonia's storage provider and dialogs, owned by a window.</summary>
public sealed class AvaloniaDialogService(Window owner) : IDialogService
{
    private static readonly FilePickerFileType Xliff = new("XLIFF files") { Patterns = ["*.xlf", "*.xliff"] };
    private static readonly FilePickerFileType TranslationMemory = new("Translation memories") { Patterns = ["*.sctm"] };
    private static readonly FilePickerFileType Termbase = new("Termbases") { Patterns = ["*.sctb"] };
    private static readonly FilePickerFileType Tmx = new("TMX files") { Patterns = ["*.tmx"] };

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

    public Task<string?> PickTranslationMemoryToOpenAsync() => PickToOpenAsync("Open translation memory", TranslationMemory);

    public Task<string?> PickTranslationMemoryToCreateAsync(string suggestedName) =>
        PickToSaveAsync("New translation memory", suggestedName, "sctm", TranslationMemory, overwritePrompt: false);

    public Task<string?> PickTermbaseToOpenAsync() => PickToOpenAsync("Open termbase", Termbase);

    public Task<string?> PickTermbaseToCreateAsync(string suggestedName) =>
        PickToSaveAsync("New termbase", suggestedName, "sctb", Termbase, overwritePrompt: false);

    public Task<string?> PickTmxToImportAsync() => PickToOpenAsync("Import TMX", Tmx);

    public Task<string?> PickTmxToExportAsync(string suggestedName) =>
        PickToSaveAsync("Export TMX", suggestedName, "tmx", Tmx, overwritePrompt: true);

    public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string fileName) =>
        new UnsavedChangesDialog(fileName).ShowDialog<UnsavedChangesChoice>(owner);

    public Task<string?> AskTargetLanguageAsync(string sourceLanguage, Func<string, string?> problem) =>
        new TargetLanguageDialog(sourceLanguage, problem).ShowDialog<string?>(owner);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        new ConfirmDialog(title, message, confirmLabel).ShowDialog<bool>(owner);

    public Task<bool> EditQaSettingsAsync(QaSettingsViewModel settings) =>
        new QaSettingsDialog(settings).ShowDialog<bool>(owner);

    public Task<InlineContent?> EditEntryAsync(EntryEditorViewModel editor) =>
        new EntryEditorDialog(editor).ShowDialog<InlineContent?>(owner);

    // The entries window, while it's open.
    private TmEntriesWindow? _entries;

    public void ShowTmEntries(TmEntriesViewModel entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (_entries is { } open && open.DataContext == entries)
        {
            open.Activate();
            return;
        }

        _entries?.Close();
        var window = new TmEntriesWindow(entries);
        window.Closed += (_, _) =>
        {
            if (_entries == window)
                _entries = null;
        };
        _entries = window;
        window.Show(owner);
    }

    private async Task<string?> PickToOpenAsync(string title, FilePickerFileType type)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [type, FilePickerFileTypes.All],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickToSaveAsync(string title, string suggestedName, string extension, FilePickerFileType type, bool overwritePrompt)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [type],
            ShowOverwritePrompt = overwritePrompt,
        });

        return file?.TryGetLocalPath();
    }

    public async Task<bool> OpenFolderAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
}
