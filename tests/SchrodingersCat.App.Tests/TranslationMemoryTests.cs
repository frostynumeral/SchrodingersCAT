using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 5 of the TM design in the view model: the Translation memory menu, remembering the TM, storing on Ctrl+Enter,
/// auto-inserting exact matches, and the languages a TM serves. Real TM files in a temp directory.
/// </summary>
public sealed class TranslationMemoryTests : IAsyncDisposable
{
    /// <summary>The same sentence twice (a repetition), then a third segment.</summary>
    private const string Repetitions = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Hello world.</source></segment></unit>
            <unit id="u2"><segment><source>Hello world.</source></segment></unit>
            <unit id="u3"><segment><source>Goodbye.</source></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();
    private readonly MemorySettings _settings = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    // Closes the TMs the tests opened before deleting their files (Windows can't delete open files).
    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    // ---------------------------------------------------------------- the menu, and remembering the TM

    [Fact]
    public async Task New_CreatesATmForTheDocumentsLanguages_AndRemembersIt()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        Assert.False(vm.CloseTranslationMemoryCommand.CanExecute(null));
        dialogs.MemoryToCreate = _files.PathOf("project.sctm");

        await vm.NewTranslationMemoryCommand.ExecuteAsync(null);

        Assert.Contains("new TM (suggested en-hu.sctm)", dialogs.Asked);
        Assert.Equal(("project.sctm", "en", "hu"), (vm.TranslationMemory!.Name, vm.TranslationMemory.SourceLanguage, vm.TranslationMemory.TargetLanguage));
        Assert.Equal("project.sctm (en → hu)", vm.MemoryLabel);
        Assert.Equal(_files.PathOf("project.sctm"), _settings.Saved.TranslationMemoryPath);
        Assert.True(vm.CloseTranslationMemoryCommand.CanExecute(null));
        Assert.True(vm.ExportTmxCommand.CanExecute(null));
    }

    [Fact]
    public async Task New_NeverReplacesAnExistingFile()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        dialogs.MemoryToCreate = _files.Write("precious.sctm", "years of work");

        await vm.NewTranslationMemoryCommand.ExecuteAsync(null);

        Assert.StartsWith("precious.sctm already exists.", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("years of work", File.ReadAllText(dialogs.MemoryToCreate));
        Assert.Null(vm.TranslationMemory);
    }

    [Fact]
    public async Task New_ForAFileWithoutATargetLanguage_AsksForItFirst()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample.Replace(" trgLang=\"hu\"", "", StringComparison.Ordinal).Replace("<target>Viszlát.</target>", "", StringComparison.Ordinal));
        dialogs.MemoryToCreate = _files.PathOf("project.sctm");

        await vm.NewTranslationMemoryCommand.ExecuteAsync(null); // cancelled: nothing happens
        Assert.Equal(["target language (source en)"], dialogs.Asked);
        Assert.Null(vm.TranslationMemory);

        dialogs.TargetLanguage = "de";
        await vm.NewTranslationMemoryCommand.ExecuteAsync(null);
        Assert.Equal("de", vm.TranslationMemory!.TargetLanguage);
        Assert.Equal("Target (de)", vm.TargetHeader);
    }

    [Fact]
    public void New_NeedsADocument() =>
        Assert.False(Create(new FakeDialogs(null)).NewTranslationMemoryCommand.CanExecute(null));

    [Fact]
    public async Task OpenAndClose_RememberAndForgetTheTm()
    {
        var path = CreateTm("project.sctm", "en", "hu");
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        dialogs.MemoryToOpen = path;

        await vm.OpenTranslationMemoryCommand.ExecuteAsync(null);
        Assert.Equal("project.sctm", vm.TranslationMemory!.Name);
        Assert.Equal(path, _settings.Saved.TranslationMemoryPath);

        await vm.CloseTranslationMemoryCommand.ExecuteAsync(null);
        Assert.Null(vm.TranslationMemory);
        Assert.Null(_settings.Saved.TranslationMemoryPath);
        Assert.False(vm.ImportTmxCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpeningAFileThatIsntATm_ShowsTheError()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        dialogs.MemoryToOpen = _files.Write("notes.sctm", "not a translation memory");

        await vm.OpenTranslationMemoryCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not open notes.sctm:", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(vm.TranslationMemory);
    }

    // The window closing: the TM is closed after its queued work, and opened again next time.
    [Fact]
    public async Task TheRememberedTm_OpensAtStartup_AndShuttingDownKeepsItRemembered()
    {
        var path = CreateTm("project.sctm", "en", "hu");
        _settings.Saved = new AppSettings(path);
        var vm = Create(new FakeDialogs(null));

        vm.RestoreTranslationMemory();
        Assert.Equal("project.sctm", vm.TranslationMemory!.Name);

        await vm.ShutDownAsync();
        Assert.Null(vm.TranslationMemory);
        Assert.Equal(path, _settings.Saved.TranslationMemoryPath);
    }

    [Theory]
    [InlineData(true, false)] // damaged: forgotten
    [InlineData(false, true)] // missing: maybe on a drive that isn't plugged in, so kept
    public async Task ARememberedTmThatCantBeOpened_IsReported(bool damaged, bool kept)
    {
        var path = damaged ? _files.Write("project.sctm", "damaged") : _files.PathOf("project.sctm");
        _settings.Saved = new AppSettings(path);
        var (vm, _) = await OpenAsync(TestFiles.Sample);

        vm.RestoreTranslationMemory();

        Assert.StartsWith("Could not open the translation memory project.sctm:", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(vm.TranslationMemory);
        Assert.Equal(kept ? path : null, _settings.Saved.TranslationMemoryPath);
    }

    // ---------------------------------------------------------------- storing on Ctrl+Enter

    [Fact]
    public async Task CtrlEnter_StoresTheSegment_WithItsContextDocumentAndUser()
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm);
        vm.SelectedSegment = vm.Segments[0];
        vm.Segments[0].TargetText = "Helló világ.";
        var before = DateTimeOffset.UtcNow;

        vm.ConfirmSegmentCommand.Execute(null);
        await SettledAsync(vm);

        var entry = Assert.Single(tm.Entries());
        Assert.Equal(("Hello world.", "Helló világ.", null, "Goodbye.", "doc.xlf", Environment.UserName),
            (entry.Source.ToPlainText(), entry.Target.ToPlainText(), entry.PreviousSource, entry.NextSource, entry.Document, entry.ChangedBy));
        Assert.InRange(entry.Changed, before, DateTimeOffset.UtcNow);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        Assert.Null(vm.Notice);
    }

    // The point of storing straight away: the repetition right below gets the translation, unconfirmed (decision 2).
    [Fact]
    public async Task ARepetitionBelow_GetsTheTranslationJustStored()
    {
        var (vm, _) = await OpenAsync(Repetitions);
        await UseNewTmAsync(vm);
        vm.SelectedSegment = vm.Segments[0];
        vm.Segments[0].TargetText = "Helló világ.";
        await vm.MatchLookup;

        vm.ConfirmSegmentCommand.Execute(null);
        await vm.MatchLookup;

        var repetition = vm.Segments[1];
        Assert.Same(repetition, vm.SelectedSegment);
        Assert.Equal("Helló világ.", repetition.TargetText);
        Assert.Equal("Initial", repetition.State);
        Assert.Equal("Inserted the 100% match automatically. Confirm it with Ctrl+Enter.", repetition.EditStatus);
    }

    [Fact]
    public async Task ReConfirmingACorrectedSegment_ReplacesItsTranslation()
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm);
        vm.SelectedSegment = vm.Segments[0];
        vm.Segments[0].TargetText = "Helló vilag.";
        vm.ConfirmSegmentCommand.Execute(null);

        vm.PreviousSegmentCommand.Execute(null);
        vm.Segments[0].TargetText = "Helló világ.";
        vm.ConfirmSegmentCommand.Execute(null);
        await SettledAsync(vm);

        Assert.Equal(["Helló világ."], tm.Entries().Select(e => e.Target.ToPlainText()));
    }

    // A segment that was confirmed when the file was opened replaces its translation from an earlier session, but not
    // the same translation stored from another file.
    [Theory]
    [InlineData("doc.xlf", new[] { "Viszontlátásra." })]
    [InlineData("other.xlf", new[] { "Viszlát.", "Viszontlátásra." })]
    public async Task ReConfirmingASegmentConfirmedInAnEarlierSession_ReplacesItsOldPairFromThisFile(string storedFrom, string[] expected)
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm);
        tm.Add(new TmEntry(InlineContent.FromText("Goodbye."), InlineContent.FromText("Viszlát.")) { Document = storedFrom });
        vm.SelectedSegment = vm.Segments[1];
        await vm.MatchLookup;

        vm.Segments[1].TargetText = "Viszontlátásra.";
        vm.ConfirmSegmentCommand.Execute(null);
        await SettledAsync(vm);

        Assert.Equal(expected, tm.Entries().Select(e => e.Target.ToPlainText()));
    }

    [Fact]
    public async Task ATargetWithCodeErrors_IsConfirmedButNotStored_AndTheNoticeSaysWhy()
    {
        var (vm, _) = await OpenAsync("""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Can't open <ph id="1" canDelete="no"/>.</source><target>Nem nyitható meg.</target></segment></unit>
                <unit id="u2"><segment><source>Next.</source></segment></unit>
              </file>
            </xliff>
            """);
        var tm = await UseNewTmAsync(vm);
        vm.SelectedSegment = vm.Segments[0];

        vm.ConfirmSegmentCommand.Execute(null);
        await SettledAsync(vm);

        Assert.Equal("Translated", vm.Segments[0].State);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        Assert.Equal("u1 was confirmed but not stored in project.sctm: its target has code errors.", vm.Notice);
        Assert.Equal(0, tm.Count);

        vm.PreviousSegmentCommand.Execute(null);
        Assert.Null(vm.Notice); // until the selection changes
    }

    // ---------------------------------------------------------------- languages (decision 3)

    [Fact]
    public async Task ATmForOtherLanguages_IsNeitherLookedUpNorStoredIn()
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm, "en", "de");
        tm.Add(new TmEntry(InlineContent.FromText("Hello world."), InlineContent.FromText("Hallo Welt.")));
        Assert.Equal("project.sctm (en → de) doesn't serve this document (en → hu).", vm.MemoryLabel);

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;
        Assert.Empty(vm.Matches);
        vm.Segments[0].TargetText = "Helló világ.";
        vm.ConfirmSegmentCommand.Execute(null);
        await SettledAsync(vm);

        Assert.Equal(1, tm.Count);
    }

    // A 101 becomes 100 and still goes in; a 100 becomes 99 and doesn't.
    [Fact]
    public async Task ATmForTheBaseLanguage_ServesARegionalVariant_OnePointLower()
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample.Replace("trgLang=\"hu\"", "trgLang=\"de-AT\"", StringComparison.Ordinal));
        var tm = await UseNewTmAsync(vm, "en", "de");
        tm.Add(new TmEntry(InlineContent.FromText("Hello world."), InlineContent.FromText("Hallo Welt.")));
        Assert.Equal("project.sctm (en → de): matches lose 1 point for this document (en → de-AT).", vm.MemoryLabel);

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;

        Assert.Equal("99%", Assert.Single(vm.Matches).Score);
        Assert.Equal("", vm.Segments[0].TargetText);
    }

    [Fact]
    public async Task AFileWithoutATargetLanguage_TakesTheTmsWhenItStores()
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample.Replace(" trgLang=\"hu\"", "", StringComparison.Ordinal).Replace("<target>Viszlát.</target>", "", StringComparison.Ordinal));
        var tm = await UseNewTmAsync(vm, "en", "hu");
        vm.SelectedSegment = vm.Segments[0];
        vm.Segments[0].TargetText = "Helló világ.";

        vm.ConfirmSegmentCommand.Execute(null);
        await SettledAsync(vm);

        Assert.Equal("Target (hu)", vm.TargetHeader);
        Assert.Equal(1, tm.Count);
    }

    // ---------------------------------------------------------------- auto-inserting exact matches (decision 2)

    [Fact]
    public async Task AnExactMatch_GoesInOnce_AndNeverOverATarget()
    {
        var (vm, _) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm);
        tm.Add(new TmEntry(InlineContent.FromText("Hello world."), InlineContent.FromText("Helló világ.")));
        tm.Add(new TmEntry(InlineContent.FromText("Goodbye."), InlineContent.FromText("Viszontlátásra.")));

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;
        var segment = vm.Segments[0];
        Assert.Equal(("Helló világ.", "Initial"), (segment.TargetText, segment.State));

        // Undone, it stays undone, even after coming back.
        segment.EditModel.Undo();
        vm.SelectedSegment = vm.Segments[1];
        await vm.MatchLookup;
        Assert.Equal("Viszlát.", vm.Segments[1].TargetText); // it had a translation
        vm.SelectedSegment = segment;
        await vm.MatchLookup;
        Assert.Equal("", segment.TargetText);
    }

    // A file's match may be machine translation scored 100: it's offered, not inserted.
    [Fact]
    public async Task TheFilesExactMatches_AreNotInsertedAutomatically()
    {
        var (vm, _) = await OpenAsync(MatchesTests.WithMatches.Replace("matchQuality=\"75\"", "matchQuality=\"100\"", StringComparison.Ordinal));

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;

        Assert.Equal("100%", Assert.Single(vm.Matches).Score);
        Assert.Equal("", vm.Segments[0].TargetText);
    }

    // ---------------------------------------------------------------- TMX

    [Fact]
    public async Task ImportingTmx_StoresWhatFits_AndReportsWhatDoesnt()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm);
        dialogs.TmxToImport = _files.Write("client.tmx", """
            <tmx version="1.4"><header srclang="en" segtype="sentence" datatype="plaintext" adminlang="en" creationtool="x" creationtoolversion="1" o-tmf="x"/>
              <body>
                <tu><tuv xml:lang="en"><seg>Hello world.</seg></tuv><tuv xml:lang="hu-HU"><seg>Helló világ.</seg></tuv></tu>
                <tu><tuv xml:lang="en"><seg>Hello world.</seg></tuv><tuv xml:lang="de"><seg>Hallo Welt.</seg></tuv></tu>
              </body>
            </tmx>
            """);

        await vm.ImportTmxCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.StartsWith("Imported 1 entry from client.tmx into project.sctm. Skipped 1 unit:", vm.Notice, StringComparison.Ordinal);
        Assert.Equal("Helló világ.", Assert.Single(tm.Entries()).Target.ToPlainText());
    }

    [Fact]
    public async Task ImportingAFileThatIsntTmx_ShowsTheError()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        await UseNewTmAsync(vm);
        dialogs.TmxToImport = _files.Write("broken.tmx", "<tmx");

        await vm.ImportTmxCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not import broken.tmx:", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportingTmx_WritesEveryEntry()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample);
        var tm = await UseNewTmAsync(vm);
        tm.Add(new TmEntry(InlineContent.FromText("Hello world."), InlineContent.FromText("Helló világ.")));
        tm.Add(new TmEntry(InlineContent.FromText("Goodbye."), InlineContent.FromText("Viszlát.")));
        dialogs.TmxToExport = _files.Write("export.tmx", "old content");

        await vm.ExportTmxCommand.ExecuteAsync(null);

        Assert.Contains("export TMX (suggested project.tmx)", dialogs.Asked);
        Assert.Equal("Exported 2 entries from project.sctm to export.tmx.", vm.Notice);
        Assert.False(File.Exists(dialogs.TmxToExport + ".tmp"));
        await using var stream = File.OpenRead(dialogs.TmxToExport);
        var import = await Tmx.ReadAsync(stream, "en", "hu", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["Helló világ.", "Viszlát."], import.Entries.Select(e => e.Target.ToPlainText()));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenAsync(string content)
    {
        var dialogs = new FakeDialogs(null);
        var vm = Create(dialogs);
        await vm.LoadAsync(_files.Write("doc.xlf", content));
        Assert.Null(vm.ErrorMessage);
        return (vm, dialogs);
    }

    // The store, and the next segment's lookup, which uses the same TM connection the test then reads.
    private static Task SettledAsync(MainWindowViewModel vm) => Task.WhenAll(vm.StoreTask, vm.MatchLookup);

    private MainWindowViewModel Create(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs, _settings);
        _viewModels.Add(vm);
        return vm;
    }

    // A real TM file, opened in the view model; returns the TM to look into.
    private async Task<ITranslationMemory> UseNewTmAsync(MainWindowViewModel vm, string source = "en", string target = "hu")
    {
        var open = OpenTranslationMemory.Create(_files.PathOf("project.sctm"), source, target);
        await vm.UseTranslationMemoryAsync(open);
        return open.Memory;
    }

    private string CreateTm(string name, string source, string target)
    {
        var path = _files.PathOf(name);
        SqliteTranslationMemory.Create(path, source, target).Dispose();
        return path;
    }

    /// <summary>Settings kept in memory.</summary>
    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Saved { get; set; } = new();

        public AppSettings Load() => Saved;

        public void Save(AppSettings settings) => Saved = settings;
    }
}
