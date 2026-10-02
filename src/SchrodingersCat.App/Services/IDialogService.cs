namespace SchrodingersCat.App.Services;

/// <summary>Asks the user for files, so view models don't depend on Avalonia's storage APIs (and tests can fake it).</summary>
public interface IFileDialogService
{
    /// <summary>The local path of the XLIFF file the user picked, or null if they cancelled.</summary>
    Task<string?> PickFileToOpenAsync();
}
