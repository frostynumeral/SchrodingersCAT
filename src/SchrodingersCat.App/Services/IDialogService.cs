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
}
