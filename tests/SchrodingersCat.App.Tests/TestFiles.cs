using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Tests;

/// <summary>A temporary directory with sample XLIFF files, deleted on dispose.</summary>
internal sealed class TestFiles : IDisposable
{
    public const string Sample = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1">
              <segment id="s1"><source>Hello world.</source></segment>
              <ignorable><source> </source></ignorable>
              <segment id="s2" state="translated"><source>Goodbye.</source><target>Viszlát.</target></segment>
            </unit>
            <unit id="u2">
              <segment><source>Press <pc id="1">OK</pc>.</source></segment>
            </unit>
          </file>
        </xliff>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("schrodingerscat-").FullName;

    public string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public string PathOf(string name) => Path.Combine(_directory, name);

    /// <summary>Makes saving to <paramref name="path"/> fail: the atomic save writes <c>&lt;path&gt;.tmp</c> first, and that's a directory.</summary>
    public static void BlockSaving(string path) => Directory.CreateDirectory(path + ".tmp");

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

/// <summary>Answers the dialogs with preset values (a null path = the user cancelled) and records what was asked.</summary>
internal sealed class FakeDialogs(string? openPath) : IDialogService
{
    public string? OpenPath { get; set; } = openPath;

    public string? SavePath { get; set; }

    public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Cancel;

    /// <summary>The answer to confirmations.</summary>
    public bool Confirm { get; set; }

    public List<string> Asked { get; } = [];

    /// <summary>The messages of the confirmations asked.</summary>
    public List<string> Messages { get; } = [];

    public Task<string?> PickFileToOpenAsync()
    {
        Asked.Add("open");
        return Task.FromResult(OpenPath);
    }

    public Task<string?> PickFileToSaveAsync(string? suggestedName)
    {
        Asked.Add($"save as (suggested {suggestedName})");
        return Task.FromResult(SavePath);
    }

    public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string fileName)
    {
        Asked.Add($"save changes to {fileName}?");
        return Task.FromResult(Choice);
    }

    /// <summary>The answer to the target language question (null = cancelled).</summary>
    public string? TargetLanguage { get; set; }

    public Task<string?> AskTargetLanguageAsync(string sourceLanguage, Func<string, string?> problem)
    {
        Asked.Add($"target language (source {sourceLanguage})");
        return Task.FromResult(TargetLanguage);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        Asked.Add($"confirm: {title}");
        Messages.Add(message);
        return Task.FromResult(Confirm);
    }

    /// <summary>The answers to the translation memory and TMX pickers (null = cancelled).</summary>
    public string? MemoryToOpen { get; set; }

    public string? MemoryToCreate { get; set; }

    public string? TmxToImport { get; set; }

    public string? TmxToExport { get; set; }

    public Task<string?> PickTranslationMemoryToOpenAsync()
    {
        Asked.Add("open TM");
        return Task.FromResult(MemoryToOpen);
    }

    public Task<string?> PickTranslationMemoryToCreateAsync(string suggestedName)
    {
        Asked.Add($"new TM (suggested {suggestedName})");
        return Task.FromResult(MemoryToCreate);
    }

    public Task<string?> PickTmxToImportAsync()
    {
        Asked.Add("import TMX");
        return Task.FromResult(TmxToImport);
    }

    public Task<string?> PickTmxToExportAsync(string suggestedName)
    {
        Asked.Add($"export TMX (suggested {suggestedName})");
        return Task.FromResult(TmxToExport);
    }

    /// <summary>Answers the QA settings: what the translator changes in them, and whether they confirm (null = cancel).</summary>
    public Func<QaSettingsViewModel, bool>? EditQaSettings { get; set; }

    public Task<bool> EditQaSettingsAsync(QaSettingsViewModel settings)
    {
        Asked.Add("QA settings");
        return Task.FromResult(EditQaSettings?.Invoke(settings) ?? false);
    }

    /// <summary>The entries window last shown.</summary>
    public TmEntriesViewModel? ShownEntries { get; private set; }

    public void ShowTmEntries(TmEntriesViewModel entries)
    {
        Asked.Add("show entries");
        ShownEntries = entries;
    }

    /// <summary>Answers the entry editor: what the translator does in it, and the translation to store (null = cancelled).</summary>
    public Func<EntryEditorViewModel, InlineContent?>? EditEntry { get; set; }

    public Task<InlineContent?> EditEntryAsync(EntryEditorViewModel editor)
    {
        Asked.Add($"edit entry: {editor.Entry.Source.ToPlainText()}");
        return Task.FromResult(EditEntry?.Invoke(editor));
    }

    /// <summary>Whether the system's file manager manages to show a folder.</summary>
    public bool FolderOpens { get; set; } = true;

    public Task<bool> OpenFolderAsync(string path)
    {
        Asked.Add($"open folder: {path}");
        return Task.FromResult(FolderOpens);
    }
}
