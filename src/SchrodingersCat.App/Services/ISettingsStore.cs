namespace SchrodingersCat.App.Services;

/// <summary>What the app remembers between sessions.</summary>
/// <param name="TranslationMemoryPath">The TM that was open when the app last changed it (decision 1: one at a time, remembered).</param>
/// <param name="DisabledQaChecks">
/// The quality checks switched off (<c>QaIssueKind</c> names, so a check added later is on, and one removed is
/// ignored); null or empty for all on.
/// </param>
/// <param name="TermbasePath">
/// The termbase that was open when the app last changed it (decision 4 of <c>docs/design/termbase.md</c>: one at a
/// time, remembered); settings saved before termbases existed simply lack it.
/// </param>
public sealed record AppSettings(string? TranslationMemoryPath = null, IReadOnlyList<string>? DisabledQaChecks = null, string? TermbasePath = null);

/// <summary>Loads and saves <see cref="AppSettings"/>, so view models don't touch the file system for them and tests can keep them in memory.</summary>
public interface ISettingsStore
{
    /// <summary>The saved settings, or the defaults if there are none or they can't be read.</summary>
    AppSettings Load();

    /// <exception cref="IOException">The settings couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The settings couldn't be written.</exception>
    void Save(AppSettings settings);
}
