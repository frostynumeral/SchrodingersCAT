using SchrodingersCat.App.Services;

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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

/// <summary>Answers the dialogs with preset values (a null path = the user cancelled) and records what was asked.</summary>
internal sealed class FakeDialogs(string? openPath) : IDialogService
{
    public string? OpenPath { get; set; } = openPath;

    public string? SavePath { get; set; }

    public UnsavedChangesChoice Choice { get; set; } = UnsavedChangesChoice.Cancel;

    public List<string> Asked { get; } = [];

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
}
