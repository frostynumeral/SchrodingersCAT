using SchrodingersCat.App.Services;

namespace SchrodingersCat.App.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Settings_SurviveSavingAndLoading_InANewFolder()
    {
        var path = _files.PathOf(Path.Combine("new folder", "settings.json"));

        new JsonSettingsStore(path).Save(new AppSettings("/work/project.sctm"));

        Assert.Equal(new AppSettings("/work/project.sctm"), new JsonSettingsStore(path).Load());
        Assert.False(File.Exists(path + ".tmp"));
    }

    // Forgetting the last TM is better than not starting.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"TranslationMemoryPath": 42}""")]
    public void AMissingOrMalformedFile_MeansTheDefaults(string? content)
    {
        var path = content is null ? _files.PathOf("missing.json") : _files.Write("settings.json", content);

        Assert.Equal(new AppSettings(), new JsonSettingsStore(path).Load());
    }
}
