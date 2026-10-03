using SchrodingersCat.App.Editing;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly TestFiles _files = new();
    private readonly Xliff20Serializer _serializer = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task Open_ListsTheTranslatableSegments()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(path));

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(path, vm.FilePath);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsDirty);
        Assert.Equal(["u1 / s1", "u1 / s2", "u2"], vm.Segments.Select(s => s.Label)); // the ignorable is not listed
        Assert.Equal(["Hello world.", "Goodbye.", "Press ⟨1⟩OK⟨/1⟩."], vm.Segments.Select(s => s.SourceText));
        Assert.Equal(["", "Viszlát.", ""], vm.Segments.Select(s => s.TargetText));
        Assert.Equal("sample.xlf — SchrödingersCAT", vm.Title);
    }

    [Fact]
    public async Task Open_Cancelled_ChangesNothing()
    {
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(null));

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Null(vm.FilePath);
        Assert.Empty(vm.Segments);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("<xliff xmlns=\"urn:oasis:names:tc:xliff:document:1.2\" version=\"1.2\"/>", "1.2")]
    [InlineData("not xml at all", "well-formed")]
    [InlineData("<!DOCTYPE xliff><xliff/>", "DTD")]
    public async Task Open_InvalidFile_ShowsTheErrorAndKeepsTheOpenDocument(string content, string expectedInMessage)
    {
        var good = _files.Write("good.xlf", TestFiles.Sample);
        var bad = _files.Write("bad.xlf", content);
        var dialogs = new FakeDialogs(good);
        var vm = new MainWindowViewModel(_serializer, dialogs);
        await vm.OpenCommand.ExecuteAsync(null);

        dialogs.OpenPath = bad;
        await vm.OpenCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not open bad.xlf:", vm.ErrorMessage);
        Assert.Contains(expectedInMessage, vm.ErrorMessage);
        Assert.Equal(good, vm.FilePath);
        Assert.Equal(3, vm.Segments.Count);
    }

    [Fact]
    public async Task Open_MissingFile_ShowsTheError()
    {
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(_files.PathOf("missing.xlf")));

        await vm.OpenCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not open missing.xlf:", vm.ErrorMessage);
        Assert.Empty(vm.Segments);
    }

    [Fact]
    public async Task EditingATarget_MarksTheDocumentDirtyAndTheSegmentTranslated()
    {
        var vm = await OpenSampleAsync();
        var segment = vm.Segments[0];
        var changed = new List<string?>();
        segment.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        segment.TargetText = "Helló világ.";

        Assert.True(vm.IsDirty);
        Assert.Equal("sample.xlf • — SchrödingersCAT", vm.Title);
        Assert.Equal("Translated", segment.State);
        Assert.Contains(nameof(SegmentViewModel.TargetText), changed);
        Assert.Contains(nameof(SegmentViewModel.State), changed);
    }

    [Fact]
    public async Task SettingTheSameTarget_IsNotAnEdit()
    {
        var vm = await OpenSampleAsync();

        vm.Segments[1].TargetText = "Viszlát.";

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SegmentsWithInlineCodes_AreEditable()
    {
        var vm = await OpenSampleAsync();
        var tagged = vm.Segments[2];
        vm.SelectedSegment = tagged;

        tagged.EditModel.InsertText("Nyomd meg: ");
        tagged.EditModel.InsertNextMissingCode();
        tagged.EditModel.InsertText("OK");

        Assert.Equal("Nyomd meg: ⟨1⟩OK⟨/1⟩", tagged.TargetText);
        Assert.Equal("Translated", tagged.State);
        Assert.True(vm.IsDirty);
        Assert.False(tagged.HasIssues);
    }

    [Fact]
    public async Task TheCodeCheck_RunsAfterEachEdit_AndMarksTheChipsInvolved()
    {
        var vm = await OpenSampleAsync();
        var tagged = vm.Segments[2];
        Assert.False(tagged.HasIssues); // not translated yet, so nothing to check

        tagged.TargetText = "Nyomja meg az OK gombot.";

        var issue = Assert.Single(tagged.Issues);
        Assert.Equal(InlineCodeIssueKind.MissingCode, issue.Kind);
        Assert.True(tagged.HasWarnings);
        Assert.False(tagged.HasErrors);
        Assert.Equal("Warning: Code '1' is missing from the target.", tagged.IssueSummary);
        Assert.All(tagged.SourceRuns.OfType<ChipRun>(), c => Assert.True(c.HasProblem)); // a missing code is marked where it is: the source

        // Clicking a source chip inserts the code; the check clears.
        vm.SelectedSegment = tagged;
        tagged.InsertCodeCommand.Execute(tagged.SourceRuns.OfType<ChipRun>().First());

        Assert.False(tagged.HasIssues);
        Assert.Null(tagged.IssueSummary);
        Assert.All(tagged.SourceRuns.OfType<ChipRun>(), c => Assert.False(c.HasProblem));
        Assert.Equal("Nyomja meg az OK gombot.⟨1⟩⟨/1⟩", tagged.TargetText);
    }

    [Fact]
    public async Task InsertingASourceCode_OnlyWorksInTheActiveSegment()
    {
        var vm = await OpenSampleAsync();
        var tagged = vm.Segments[2];
        var chip = tagged.SourceRuns.OfType<ChipRun>().First();

        Assert.False(tagged.InsertCodeCommand.CanExecute(chip));

        vm.SelectedSegment = tagged;
        Assert.True(tagged.InsertCodeCommand.CanExecute(chip));
    }

    [Fact]
    public async Task AnEdit_UpdatesTheIssuesOfTheUnitsOtherSegments()
    {
        // A pair spanning two segments: deleting its closing half in s2 leaves its opening half in s1 unpaired.
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(_files.Write("spanning.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1">
                  <segment id="s1"><source><sc id="1"/>Bold</source><target><sc id="1"/>Félkövér</target></segment>
                  <segment id="s2"><source> text<ec startRef="1"/>.</source><target> szöveg<ec startRef="1"/>.</target></segment>
                </unit>
              </file>
            </xliff>
            """)));
        await vm.OpenCommand.ExecuteAsync(null);
        var (first, second) = (vm.Segments[0], vm.Segments[1]);
        Assert.False(first.HasIssues);
        var changed = new List<string?>();
        first.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        second.EditModel.MoveCaret(8); // after the closing half
        second.EditModel.Delete(DeleteDirection.Backward);

        var issue = Assert.Single(first.Issues);
        Assert.Equal(InlineCodeIssueKind.Unpaired, issue.Kind);
        Assert.True(first.HasErrors);
        Assert.Equal(["1"], first.TargetProblemCodes);
        Assert.True(Assert.IsType<ChipRun>(first.TargetRuns[0]).HasProblem);
        Assert.Contains(nameof(SegmentViewModel.HasErrors), changed);
        Assert.Contains(nameof(SegmentViewModel.TargetRuns), changed);
        Assert.False(second.HasIssues);
    }

    [Fact]
    public async Task Save_WritesTheEditsBackToTheFile()
    {
        var vm = await OpenSampleAsync();
        vm.Segments[0].TargetText = "Helló világ.";
        vm.Segments[1].TargetText = ""; // clearing a target means "not translated"

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Null(vm.ErrorMessage);
        var saved = (await _serializer.ReadFileAsync(vm.FilePath!, TestContext.Current.CancellationToken)).Units.SelectMany(u => u.TranslatableSegments).ToList();
        Assert.Equal("Helló világ.", saved[0].Target!.ToPlainText());
        Assert.Equal(Core.Models.SegmentState.Translated, saved[0].State);
        Assert.Null(saved[1].Target);
        Assert.Equal("Press OK.", saved[2].Source.ToPlainText()); // untouched segments, codes included, survive
        Assert.True(saved[2].Source.HasTags);
    }

    [Fact]
    public async Task Save_Failure_ShowsTheErrorAndKeepsTheEdits()
    {
        var path = _files.Write("blocked.xlf", TestFiles.Sample);
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(path));
        await vm.OpenCommand.ExecuteAsync(null);
        var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        vm.Segments[0].TargetText = "Helló világ.";
        TestFiles.BlockSaving(path);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not save blocked.xlf:", vm.ErrorMessage);
        Assert.True(vm.IsDirty);
        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)); // the atomic save left the file alone
    }

    // ---------------------------------------------------------------- target language

    private const string NoTargetLanguage = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en">
          <file id="f1"><unit id="u1"><segment><source>Hello.</source></segment></unit></file>
        </xliff>
        """;

    [Fact]
    public async Task Save_WithoutATargetLanguage_AsksForIt_AndSaves()
    {
        var path = _files.Write("no-trglang.xlf", NoTargetLanguage);
        var dialogs = new FakeDialogs(path) { TargetLanguage = " hu " };
        var vm = new MainWindowViewModel(_serializer, dialogs);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Equal("Target", vm.TargetHeader);
        vm.Segments[0].TargetText = "Szia.";
        dialogs.Asked.Clear();

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["target language (source en)"], dialogs.Asked);
        Assert.False(vm.IsDirty);
        Assert.Equal("Target (hu)", vm.TargetHeader);
        var saved = await _serializer.ReadFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("hu", saved.TargetLanguage);
        Assert.Equal("Szia.", saved.Units.Single().Segments[0].Target!.ToPlainText());

        // Asked once: the document has a target language now.
        vm.Segments[0].TargetText = "Helló.";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Asked);
    }

    [Fact]
    public async Task Save_WithoutATargetLanguage_Cancelled_SavesNothing()
    {
        var path = _files.Write("no-trglang.xlf", NoTargetLanguage);
        var dialogs = new FakeDialogs(path) { TargetLanguage = null };
        var vm = new MainWindowViewModel(_serializer, dialogs);
        await vm.OpenCommand.ExecuteAsync(null);
        vm.Segments[0].TargetText = "Szia.";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsDirty);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(NoTargetLanguage, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_WithoutATargetLanguage_OrTargets_DoesNotAsk()
    {
        var path = _files.Write("no-trglang.xlf", NoTargetLanguage);
        var dialogs = new FakeDialogs(path);
        var vm = new MainWindowViewModel(_serializer, dialogs);
        await vm.OpenCommand.ExecuteAsync(null);
        vm.Segments[0].TargetText = "Szia.";
        vm.Segments[0].TargetText = ""; // dirty, but no target
        dialogs.Asked.Clear();

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Asked);
        Assert.False(vm.IsDirty);
    }

    [Theory]
    [InlineData("hu", null)]
    [InlineData(" de-AT ", null)]
    [InlineData("zh-Hant-TW", null)]
    [InlineData("", "Enter a language code, such as hu or de-AT.")]
    [InlineData("hu_HU", "'hu_HU' isn't a language code. Use letters, digits and hyphens, such as hu or de-AT.")]
    [InlineData("magyarország", "'magyarország' isn't a language code. Use letters, digits and hyphens, such as hu or de-AT.")]
    [InlineData("EN", "That's the source language (en).")]
    public void LanguageTag_AcceptsXsLanguageCodesOtherThanTheSource(string tag, string? problem) =>
        Assert.Equal(problem, LanguageTag.Problem(tag, "en"));

    [Fact]
    public async Task DismissError_ClearsTheMessage()
    {
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(_files.PathOf("missing.xlf")));
        await vm.OpenCommand.ExecuteAsync(null);

        vm.DismissErrorCommand.Execute(null);

        Assert.Null(vm.ErrorMessage);
    }

    private async Task<MainWindowViewModel> OpenSampleAsync()
    {
        var vm = new MainWindowViewModel(_serializer, new FakeDialogs(_files.Write("sample.xlf", TestFiles.Sample)));
        await vm.OpenCommand.ExecuteAsync(null);
        return vm;
    }
}
