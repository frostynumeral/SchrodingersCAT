using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;
using static SchrodingersCat.App.Tests.TermbaseFiles;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 5 of <c>docs/design/termbase.md</c> in the view model: the Termbase menu, remembering and loading the termbase,
/// the terms list and the marks, Ctrl+T's choices, inserting, and the checks using it. Real termbase files.
/// </summary>
public sealed class TermbaseViewModelTests : IAsyncDisposable
{
    /// <summary>
    /// u1 has only the forbidden translation, u2 is untranslated with two terms, u3 has the preferred translation (with
    /// a suffix), u4 has neither the termbase's nor the glossary's, u5 has no terms.
    /// </summary>
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:gls="urn:oasis:names:tc:xliff:glossary:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Open the file.</source><target>Nyisd meg az aktát.</target></segment></unit>
            <unit id="u2"><segment><source>Save the file manager settings.</source></segment></unit>
            <unit id="u3"><segment><source>Close the file.</source><target>Zárd be a fájlt.</target></segment></unit>
            <unit id="u4">
              <gls:glossary><gls:glossEntry><gls:term>file</gls:term><gls:translation>dosszié</gls:translation></gls:glossEntry></gls:glossary>
              <segment><source>Delete the file.</source><target>Töröld a dokumentumot.</target></segment>
            </unit>
            <unit id="u5"><segment><source>Nothing to see here.</source><target>Semmi.</target></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();
    private readonly MemorySettings _settings = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    // Closes the termbases the tests opened before deleting their files.
    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    // ---------------------------------------------------------------- the menu, and remembering the termbase

    [Fact]
    public async Task New_CreatesATermbaseForTheDocumentsLanguages_AndRemembersIt()
    {
        var (vm, dialogs) = await OpenAsync(Document);
        Assert.False(vm.ShowsTerms);
        Assert.False(vm.CloseTermbaseCommand.CanExecute(null));
        dialogs.TermbaseToCreate = _files.PathOf("en-hu.sctb");

        await vm.NewTermbaseCommand.ExecuteAsync(null);

        Assert.Contains("new termbase (suggested en-hu.sctb)", dialogs.Asked);
        Assert.Equal(("en-hu", "en", "hu"), (vm.Termbase!.Name, vm.Termbase.SourceLanguage, vm.Termbase.TargetLanguage));
        Assert.Equal("en-hu (en → hu)", vm.TermbaseLabel);
        Assert.Equal(_files.PathOf("en-hu.sctb"), _settings.Saved.TermbasePath);
        Assert.True(vm.ShowsTerms);
        Assert.True(vm.CloseTermbaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task New_NeverReplacesAnExistingFile()
    {
        var (vm, dialogs) = await OpenAsync(Document);
        dialogs.TermbaseToCreate = _files.Write("client.sctb", "a client's terminology");

        await vm.NewTermbaseCommand.ExecuteAsync(null);

        Assert.StartsWith("client.sctb already exists.", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("a client's terminology", File.ReadAllText(dialogs.TermbaseToCreate));
        Assert.Null(vm.Termbase);
    }

    [Fact]
    public async Task New_ForAFileWithoutATargetLanguage_AsksForItFirst()
    {
        var (vm, dialogs) = await OpenAsync(TestFiles.Sample.Replace(" trgLang=\"hu\"", "", StringComparison.Ordinal).Replace("<target>Viszlát.</target>", "", StringComparison.Ordinal));
        dialogs.TermbaseToCreate = _files.PathOf("project.sctb");

        await vm.NewTermbaseCommand.ExecuteAsync(null); // cancelled: nothing happens
        Assert.Equal(["target language (source en)"], dialogs.Asked);
        Assert.Null(vm.Termbase);

        dialogs.TargetLanguage = "de";
        await vm.NewTermbaseCommand.ExecuteAsync(null);
        Assert.Equal("de", vm.Termbase!.TargetLanguage);
    }

    [Fact]
    public void New_NeedsADocument() =>
        Assert.False(Create(new FakeDialogs(null)).NewTermbaseCommand.CanExecute(null));

    // Section 3: loading runs in the background; until it's done, "Loading…", no marks, and the file's glossary only.
    [Fact]
    public async Task Open_LoadsInTheBackground_ThenMarksListsAndChecks()
    {
        var path = Write(_files.PathOf("client.sctb"), "en", "hu", Client);
        var (vm, dialogs) = await OpenAsync(Document);
        dialogs.TermbaseToOpen = path;
        var gate = new TaskCompletionSource();
        vm.OpenTermbaseFile = async (p, ct) =>
        {
            await gate.Task;
            return await OpenTermbase.OpenAsync(p, ct);
        };
        vm.SelectedSegment = vm.Segments[1];

        var opening = vm.OpenTermbaseCommand.ExecuteAsync(null);
        Assert.Equal("client.sctb", vm.LoadingTermbase);
        Assert.True(vm.ShowsTerms);
        Assert.True(vm.CloseTermbaseCommand.CanExecute(null));
        Assert.Equal("Loading client.sctb…", vm.TermbaseLabel);
        Assert.Equal("Loading the termbase…", vm.TermsStatus);
        Assert.Empty(vm.Terms);
        Assert.DoesNotContain(vm.Segments.SelectMany(s => s.SourceRuns).OfType<TextRun>(), r => r.Annotations.HasFlag(AnnotationStyle.TermbaseTerm));
        Assert.Empty(TermbaseIssues(vm));
        Assert.StartsWith("The file's glossary translates", Assert.Single(TermIssues(vm, 3)).Message, StringComparison.Ordinal); // meanwhile

        gate.SetResult();
        await opening;
        await vm.TermCheck;

        Assert.Null(vm.LoadingTermbase);
        Assert.Equal("client (en → hu)", vm.TermbaseLabel);
        Assert.Equal(path, _settings.Saved.TermbasePath);
        Assert.Equal(["file manager", "file"], vm.Terms.Select(t => t.SourceText));
        Assert.Null(vm.TermsStatus);
        // "file" is in both terms, so its tooltip has both.
        var marked = vm.Segments[1].SourceRuns.OfType<TextRun>().Where(r => r.Annotations.HasFlag(AnnotationStyle.TermbaseTerm)).ToList();
        Assert.Equal(["file", " manager"], marked.Select(r => r.Text));
        Assert.Equal(
            "Termbase “client”: file manager\n→ fájlkezelő\n\nTermbase “client”: file\n→ fájl, állomány\nForbidden: akta\nComputer files only.",
            marked[0].TermToolTip);
        Assert.Equal("Termbase “client”: file manager\n→ fájlkezelő", marked[1].TermToolTip);
        Assert.Equal(
            "The termbase “client” translates “file” as “állomány” or “fájl”, which isn't in the target.",
            Assert.Single(TermIssues(vm, 0)).Message);
    }

    [Fact]
    public async Task OpeningADamagedFile_ShowsTheError_AndKeepsTheOpenTermbase()
    {
        var (vm, dialogs) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "client", Client);
        var open = vm.Termbase;
        dialogs.TermbaseToOpen = _files.Write("notes.sctb", "not a termbase");

        await vm.OpenTermbaseCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not open the termbase notes.sctb:", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Same(open, vm.Termbase);
        Assert.Null(vm.LoadingTermbase);
    }

    [Fact]
    public async Task Restoring_ForgetsADamagedTermbase_ButKeepsAMissingOne()
    {
        _settings.Saved = new AppSettings(TermbasePath: _files.Write("damaged.sctb", "not a termbase"));
        var vm = Create(new FakeDialogs(null));
        vm.RestoreTermbase();
        await vm.TermbaseLoad;
        Assert.StartsWith("Could not open the termbase damaged.sctb:", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(_settings.Saved.TermbasePath);

        var missing = _files.PathOf("on-a-usb-stick.sctb");
        _settings.Saved = new AppSettings(TermbasePath: missing);
        vm = Create(new FakeDialogs(null));
        vm.RestoreTermbase();
        await vm.TermbaseLoad;
        Assert.StartsWith("Could not open the termbase on-a-usb-stick.sctb:", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(missing, _settings.Saved.TermbasePath);
    }

    [Fact]
    public async Task Restoring_LoadsTheRememberedTermbase_WithoutRememberingItAgain()
    {
        var path = Write(_files.PathOf("client.sctb"), "en", "hu", Client);
        var store = new CountingSettings { Saved = new AppSettings(TermbasePath: path) };
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null), store);
        _viewModels.Add(vm);
        await vm.LoadAsync(_files.Write("doc.xlf", Document));

        vm.RestoreTermbase();
        await vm.TermbaseLoad;
        await vm.TermCheck;

        Assert.Equal("client", vm.Termbase!.Name);
        Assert.Equal(0, store.Saves);
        Assert.Single(TermIssues(vm, 0));
    }

    // A newer Open cancels a load that is still running; a termbase it opened all the same is closed.
    [Fact]
    public async Task ASecondOpen_CancelsTheFirst_AndCloseCancelsALoad()
    {
        var second = Write(_files.PathOf("second.sctb"), "en", "hu", Client);
        var (vm, dialogs) = await OpenAsync(Document);
        var gate = new TaskCompletionSource();
        var first = new Tracking(entries: Client);
        vm.OpenTermbaseFile = async (p, ct) =>
        {
            if (p == second)
                return await OpenTermbase.OpenAsync(p, ct);
            await gate.Task;
            return new OpenTermbase(first, "first");
        };

        dialogs.TermbaseToOpen = _files.PathOf("first.sctb");
        var opening = vm.OpenTermbaseCommand.ExecuteAsync(null);
        dialogs.TermbaseToOpen = second;
        await vm.OpenTermbaseCommand.ExecuteAsync(null);
        Assert.Equal("second", vm.Termbase!.Name);

        gate.SetResult();
        await opening;
        Assert.Equal("second", vm.Termbase!.Name);
        Assert.True(first.IsDisposed);
        Assert.Null(vm.LoadingTermbase);
        Assert.Equal(second, _settings.Saved.TermbasePath);

        // Close while loading: the load stops, and so does the termbase open.
        var third = new Tracking(entries: Client);
        gate = new TaskCompletionSource();
        vm.OpenTermbaseFile = async (_, _) =>
        {
            await gate.Task;
            return new OpenTermbase(third, "third");
        };
        opening = vm.OpenTermbaseCommand.ExecuteAsync(null);
        Assert.NotNull(vm.LoadingTermbase);
        await vm.CloseTermbaseCommand.ExecuteAsync(null);
        Assert.Null(vm.LoadingTermbase);
        Assert.Null(vm.Termbase);
        gate.SetResult();
        await opening;
        Assert.True(third.IsDisposed);
        Assert.Null(vm.Termbase);
        Assert.Null(_settings.Saved.TermbasePath);
    }

    [Fact]
    public async Task Close_ClearsTheTermsTheMarksAndTheTermbasesIssues()
    {
        var (vm, _) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "client", Client);
        vm.SelectedSegment = vm.Segments[1];
        Assert.NotEmpty(vm.Terms);
        Assert.NotEmpty(TermbaseIssues(vm));

        await vm.CloseTermbaseCommand.ExecuteAsync(null);
        await vm.TermCheck;

        Assert.Null(vm.Termbase);
        Assert.False(vm.ShowsTerms);
        Assert.Empty(vm.Terms);
        Assert.Null(vm.TermsStatus);
        Assert.DoesNotContain(vm.Segments[1].SourceRuns.OfType<TextRun>(), r => r.Annotations.HasFlag(AnnotationStyle.TermbaseTerm));
        Assert.Empty(TermbaseIssues(vm));
        Assert.Null(_settings.Saved.TermbasePath);
    }

    // The window closing: the termbase is closed, and opened again next time.
    [Fact]
    public async Task ShutDown_ClosesTheTermbase_AndKeepsItRemembered()
    {
        var path = Write(_files.PathOf("client.sctb"), "en", "hu", Client);
        var (vm, dialogs) = await OpenAsync(Document);
        dialogs.TermbaseToOpen = path;
        await vm.OpenTermbaseCommand.ExecuteAsync(null);

        await vm.ShutDownAsync();

        Assert.Null(vm.Termbase);
        Assert.Equal(path, _settings.Saved.TermbasePath);
        using (SqliteTermbase.Open(path, TestContext.Current.CancellationToken))
        {
            // it can be opened again: nothing holds it
        }
    }

    // ---------------------------------------------------------------- the checks

    [Fact]
    public async Task TheCheck_UsesTheTermbase_ForbiddenTranslationsNeverSatisfyIt_AndOriginsAreMerged()
    {
        var (vm, _) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "client", Client);

        Assert.Single(TermIssues(vm, 0)); // only the forbidden "akta"
        Assert.Empty(TermIssues(vm, 2)); // "fájlt" has the preferred translation
        Assert.Equal(
            "The file's glossary and the termbase “client” translate “file” as “dosszié” or “állomány” or “fájl”, which isn't in the target.",
            Assert.Single(TermIssues(vm, 3)).Message);
        Assert.Empty(TermIssues(vm, 4));
        Assert.Equal(IssueSeverity.Warning, TermIssues(vm, 0)[0].Severity);
    }

    // Decision 11: a termbase serves its own languages only; one that doesn't serve is shown, and used for nothing.
    [Fact]
    public async Task ATermbaseForOtherLanguages_ServesNothing_UntilTheDocumentsLanguageFits()
    {
        var (vm, _) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "german", Client, target: "de");
        vm.SelectedSegment = vm.Segments[1];

        Assert.Equal("german (en → de) doesn't serve this document (en → hu).", vm.TermbaseLabel);
        Assert.Empty(vm.Terms);
        Assert.Empty(vm.Segments[1].TermHits);
        Assert.Empty(TermbaseIssues(vm));
        Assert.Null(vm.DocumentTerms!.Source);
    }

    [Fact]
    public async Task SettingTheTargetLanguageLater_DecidesWhetherTheTermbaseServes()
    {
        var (vm, dialogs) = await OpenAsync(Document.Replace(" trgLang=\"hu\"", "", StringComparison.Ordinal).Replace("<target>Nyisd meg az aktát.</target>", "", StringComparison.Ordinal)
            .Replace("<target>Zárd be a fájlt.</target>", "", StringComparison.Ordinal).Replace("<target>Töröld a dokumentumot.</target>", "", StringComparison.Ordinal)
            .Replace("<target>Semmi.</target>", "", StringComparison.Ordinal));
        await UseTermbaseAsync(vm, "german", Client, target: "de");
        vm.SelectedSegment = vm.Segments[1];
        Assert.NotEmpty(vm.Terms); // judged by the source language alone

        dialogs.TargetLanguage = "hu";
        dialogs.TermbaseToCreate = null;
        await vm.NewTermbaseCommand.ExecuteAsync(null); // asks for the target language, then the picker is cancelled
        await vm.TermCheck;

        Assert.Equal("Target (hu)", vm.TargetHeader);
        Assert.Empty(vm.Terms);
        Assert.Contains("doesn't serve", vm.TermbaseLabel, StringComparison.Ordinal);
    }

    // An edit during the background re-check is newer than the re-check's result for its unit; a superseded re-check
    // applies nothing.
    [Fact]
    public async Task TheBackgroundRecheck_KeepsNewerResults_AndASupersededOneAppliesNothing()
    {
        var (vm, _) = await OpenAsync(Document);
        var gate = new TaskCompletionSource();
        var calls = 0;
        vm.RunOffUiThread = work => Interlocked.Increment(ref calls) == 1 ? gate.Task.ContinueWith(_ => work(), TaskScheduler.Default) : Task.Run(work);

        await vm.UseTermbaseAsync(CreateTermbase("client", Client));
        var check = vm.TermCheck;
        Assert.False(check.IsCompleted);
        vm.Segments[0].TargetText = "Nyisd meg a fájlt."; // checked at once with the termbase
        Assert.Empty(TermIssues(vm, 0));
        Assert.DoesNotContain(TermIssues(vm, 3), i => i.Message.Contains("termbase", StringComparison.Ordinal));

        gate.SetResult();
        await check;
        Assert.Empty(TermIssues(vm, 0));
        Assert.Contains("termbase “client”", Assert.Single(TermIssues(vm, 3)).Message, StringComparison.Ordinal);

        // Superseded: a re-check held back while the termbase closes and its own re-check runs.
        gate = new TaskCompletionSource();
        calls = 0;
        await vm.UseTermbaseAsync(CreateTermbase("other", Client));
        var held = vm.TermCheck;
        await vm.CloseTermbaseCommand.ExecuteAsync(null);
        await vm.TermCheck;
        gate.SetResult();
        await held;
        Assert.Empty(TermbaseIssues(vm));
    }

    [Fact]
    public async Task EachDocument_HasItsOwnTermSource()
    {
        var (vm, _) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "client", Client);
        var first = vm.DocumentTerms!;
        Assert.NotNull(first.Source);

        await vm.LoadAsync(_files.Write("other.xlf", Document));
        await vm.TermCheck;

        Assert.NotSame(first, vm.DocumentTerms);
        Assert.NotNull(vm.DocumentTerms!.Source);
        Assert.NotSame(first.Source, vm.DocumentTerms.Source);
        Assert.Same(vm.Termbase!.Index, vm.DocumentTerms.Index);
        Assert.Single(TermIssues(vm, 0));
    }

    [Fact]
    public async Task TheQaReport_IsListedAgainAfterTheTermbaseLoads_WithoutSwitchingTabs()
    {
        var (vm, _) = await OpenAsync(Document);
        vm.Qa.RunCommand.Execute(null);
        Assert.DoesNotContain(vm.Qa.Rows.OfType<QaReportEntry>(), e => e.Message.Contains("termbase", StringComparison.Ordinal));
        vm.PaneTab = 0;

        await UseTermbaseAsync(vm, "client", Client);

        Assert.Equal(0, vm.PaneTab);
        Assert.Contains(vm.Qa.Rows.OfType<QaReportEntry>(), e => e.Segment == vm.Segments[0] && e.Message.Contains("termbase “client”", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Ctrl+T and inserting

    [Fact]
    public async Task TheTermPicker_OffersAllowedTranslations_PreferredFirst_EachOnce()
    {
        var (vm, _) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "client",
        [
            .. Client,
            Entry("settings", ("beállítások", TermStatus.Unspecified), ("  ", TermStatus.Preferred)),
            Entry("file", ("fájl", TermStatus.Preferred), ("iktató", TermStatus.Admitted)), // a homonym
        ]);
        vm.SelectedSegment = vm.Segments[1];

        Assert.Equal(
            ["fájlkezelő", "fájl", "állomány", "iktató", "beállítások"],
            vm.TermPickerChoices().Select(c => c.Text));
        Assert.Equal("file manager", vm.TermPickerChoices()[0].Source);
        Assert.Contains("akta (forbidden)", vm.TermPickerChoices()[1].Description, StringComparison.Ordinal);
        Assert.Equal(["fájl", "állomány", "akta"], vm.Terms[1].Translations.Select(t => t.Text));
        Assert.Equal("fájl", vm.Terms[1].FirstAllowed);
        Assert.Equal("Computer files only.", vm.Terms[1].Notes);
    }

    [Fact]
    public async Task WhenThePickerHasNothing_TheStatusLineSaysWhy()
    {
        var (vm, dialogs) = await OpenAsync(Document);
        vm.SelectedSegment = vm.Segments[1];
        Assert.Empty(vm.TermPickerChoices());
        Assert.Equal("No termbase is open. Open one with Termbase → Open…", vm.SelectedSegment.EditStatus);

        var gate = new TaskCompletionSource();
        vm.OpenTermbaseFile = async (_, _) =>
        {
            await gate.Task;
            return new OpenTermbase(new Tracking("en", "de", Client), "german");
        };
        dialogs.TermbaseToOpen = _files.PathOf("german.sctb");
        var opening = vm.OpenTermbaseCommand.ExecuteAsync(null);
        Assert.Empty(vm.TermPickerChoices());
        Assert.Equal("The termbase is still loading.", vm.SelectedSegment.EditStatus);
        gate.SetResult();
        await opening;

        Assert.Empty(vm.TermPickerChoices());
        Assert.Equal("The termbase “german” doesn't serve this document's languages.", vm.SelectedSegment.EditStatus);

        await UseTermbaseAsync(vm, "client", [Entry("settings", ("beállítás", TermStatus.Forbidden))]);
        Assert.Empty(vm.TermPickerChoices());
        Assert.Equal("The terms found have no translations to insert.", vm.SelectedSegment.EditStatus);

        vm.SelectedSegment = vm.Segments[4];
        Assert.Empty(vm.TermPickerChoices());
        Assert.Equal("No terms found in this segment's source.", vm.SelectedSegment.EditStatus);
    }

    [Fact]
    public async Task InsertingATerm_ReplacesTheSelection_TranslatesTheSegment_AndCanBeUndone()
    {
        var (vm, _) = await OpenAsync(Document);
        await UseTermbaseAsync(vm, "client", Client);
        var segment = vm.Segments[1];
        vm.SelectedSegment = segment;
        Assert.Equal("Initial", segment.State);

        vm.InsertTermCommand.Execute(vm.Terms[0]);
        Assert.Equal("fájlkezelő", segment.TargetText);
        Assert.Equal("Translated", segment.State);
        Assert.Equal("Inserted the term “fájlkezelő”.", segment.EditStatus);
        segment.EditModel.Undo();
        Assert.Null(segment.Target);
        Assert.Equal("Translated", segment.State); // nothing moves it back

        segment.TargetText = "Mentsd a XXX beállításait.";
        segment.EditModel.Select(9, 12);
        segment.InsertTerm("fájlkezelő");
        Assert.Equal("Mentsd a fájlkezelő beállításait.", segment.TargetText);
        Assert.False(vm.InsertTermCommand.CanExecute(new TermViewModel(
            new TermHit(new TermEntryId(9), Entry("x", ("y", TermStatus.Forbidden)), [0], []), vm.InsertTermCommand)));
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<QaIssue> TermbaseIssues(MainWindowViewModel vm) =>
        vm.Segments.SelectMany(s => s.Issues).Select(i => i.Qa).OfType<QaIssue>()
            .Where(i => i.Kind == QaIssueKind.Terminology && i.Message.Contains("termbase", StringComparison.Ordinal));

    private static List<QaIssue> TermIssues(MainWindowViewModel vm, int segment) =>
        [.. vm.Segments[segment].Issues.Select(i => i.Qa).OfType<QaIssue>().Where(i => i.Kind == QaIssueKind.Terminology)];

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenAsync(string content)
    {
        var dialogs = new FakeDialogs(null);
        var vm = Create(dialogs);
        await vm.LoadAsync(_files.Write("doc.xlf", content));
        Assert.Null(vm.ErrorMessage);
        return (vm, dialogs);
    }

    private MainWindowViewModel Create(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs, _settings);
        _viewModels.Add(vm);
        return vm;
    }

    // A real termbase file with these entries, in use in the view model, its re-check done.
    private async Task UseTermbaseAsync(MainWindowViewModel vm, string name, TermEntry[] entries, string target = "hu")
    {
        await vm.UseTermbaseAsync(CreateTermbase(name, entries, target));
        await vm.TermCheck;
    }

    private OpenTermbase CreateTermbase(string name, TermEntry[] entries, string target = "hu")
    {
        var path = _files.PathOf($"{name}.sctb");
        File.Delete(path);
        var termbase = OpenTermbase.Create(path, "en", target);
        termbase.Termbase.AddRange(entries);
        return termbase;
    }

    /// <summary>Settings kept in memory.</summary>
    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Saved { get; set; } = new();

        public AppSettings Load() => Saved;

        public void Save(AppSettings settings) => Saved = settings;
    }

    /// <summary>Settings kept in memory, counting the saves.</summary>
    private sealed class CountingSettings : ISettingsStore
    {
        public AppSettings Saved { get; set; } = new();

        public int Saves { get; private set; }

        public AppSettings Load() => Saved;

        public void Save(AppSettings settings)
        {
            Saved = settings;
            Saves++;
        }
    }
}
