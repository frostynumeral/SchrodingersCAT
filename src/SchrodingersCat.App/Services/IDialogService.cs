using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Services;

/// <summary>What the user wants to do with unsaved changes.</summary>
public enum UnsavedChangesChoice
{
    /// <summary>
    /// Keep the changes and abort what was about to happen. Deliberately the default value: a dialog closed without
    /// an answer (title bar, Escape) returns default, and that must never mean Save or Don't save.
    /// </summary>
    Cancel,
    Save,
    DontSave,
}

/// <summary>
/// Asks the user things (files to open or save, what to do with unsaved changes, confirmations), so view models don't depend on
/// Avalonia's windowing and storage APIs, and tests can answer instead.
/// </summary>
public interface IDialogService
{
    /// <summary>The local path of the XLIFF file the user picked, or null if they cancelled.</summary>
    Task<string?> PickFileToOpenAsync();

    /// <summary>The local path to save to (the user confirmed overwriting it), or null if they cancelled.</summary>
    Task<string?> PickFileToSaveAsync(string? suggestedName);

    /// <summary>Asks whether to save the unsaved changes to <paramref name="fileName"/> first.</summary>
    Task<UnsavedChangesChoice> AskToSaveChangesAsync(string fileName);

    /// <summary>
    /// Asks for the target language of a document that has none. <paramref name="problem"/> says what's wrong with a
    /// typed tag (null if it's fine); the dialog only accepts a tag without a problem. Null if the user cancelled.
    /// </summary>
    Task<string?> AskTargetLanguageAsync(string sourceLanguage, Func<string, string?> problem);

    /// <summary>Asks for confirmation; true only if the user chose <paramref name="confirmLabel"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

    /// <summary>
    /// Lets the user edit a TM entry's translation. The new translation (one <see cref="EntryEditorViewModel.TrySave"/>
    /// accepted), or null if they cancelled.
    /// </summary>
    Task<InlineContent?> EditEntryAsync(EntryEditorViewModel editor);

    /// <summary>
    /// Shows the window with the TM's entries (not modal), or brings it to the front if it's showing already. It
    /// closes on <see cref="TmEntriesViewModel.CloseRequested"/>.
    /// </summary>
    void ShowTmEntries(TmEntriesViewModel entries);

    /// <summary>The local path of the translation memory (<c>*.sctm</c>) the user picked to open, or null if they cancelled.</summary>
    Task<string?> PickTranslationMemoryToOpenAsync();

    /// <summary>
    /// The local path for a new translation memory, or null if they cancelled. It doesn't ask about replacing an
    /// existing file: the caller refuses existing files rather than lose a TM.
    /// </summary>
    Task<string?> PickTranslationMemoryToCreateAsync(string suggestedName);

    /// <summary>The local path of the TMX file the user picked to import, or null if they cancelled.</summary>
    Task<string?> PickTmxToImportAsync();

    /// <summary>The local path to export TMX to (the user confirmed overwriting it), or null if they cancelled.</summary>
    Task<string?> PickTmxToExportAsync(string suggestedName);

    /// <summary>
    /// Shows the folder in the system's file manager, creating it if needed; false if that couldn't be done. True only
    /// means it was tried: on Linux, Avalonia counts it as done once <c>xdg-open</c> has started, even if it then fails.
    /// </summary>
    Task<bool> OpenFolderAsync(string path);
}
