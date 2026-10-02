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

/// <summary>Answers the open dialog with a preset path (null = the user cancelled).</summary>
internal sealed class FakeFileDialogs(string? path) : IFileDialogService
{
    public string? Path { get; set; } = path;

    public Task<string?> PickFileToOpenAsync() => Task.FromResult(Path);
}
