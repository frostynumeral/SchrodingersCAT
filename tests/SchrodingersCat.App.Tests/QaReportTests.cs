using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

/// <summary>Step 4 of <c>docs/design/qa-checks.md</c>: the QA tab (F8), dismissing, and the settings.</summary>
public sealed class QaReportTests : IDisposable
{
    /// <summary>
    /// u1 a wrong number, u2 a missing code and a missing full stop, u3 untranslated, u4 and u5 the same source
    /// translated two ways.
    /// </summary>
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Copy 3 files.</source><target>Másolj 4 fájlt.</target></segment></unit>
            <unit id="u2"><segment><source>Press <pc id="1">OK</pc>.</source><target>Nyomd meg az OK gombot</target></segment></unit>
            <unit id="u3"><segment><source>Open the file.</source></segment></unit>
            <unit id="u4"><segment><source>Save.</source><target>Mentés.</target></segment></unit>
            <unit id="u5"><segment><source>Save.</source><target>Mentsd.</target></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task F8_ListsEveryIssue_GroupedByCheck_CodesFirst()
    {
        var (vm, _) = await OpenAsync();

        vm.Qa.RunCommand.Execute(null);

        Assert.Equal(2, vm.PaneTab);
        Assert.Equal(
        [
            "Inline codes (1)", "u2: Code '1' is missing from the target.",
            "Numbers (2)", "u1: The number 3 isn't in the target.", "u1: The target has the number 4, which the source doesn't.",
            "Untranslated segments (1)", "u3: The segment isn't translated.",
            "Inconsistent translations (2)", "u4: “Save.” is translated 2 ways in this document.", "u5: “Save.” is translated 2 ways in this document.",
            "End punctuation (1)", "u2: The source ends with “.”, the target with no punctuation.",
        ], vm.Qa.Rows.Select(Text));
        Assert.Equal("7 issues in 5 segments.", vm.Qa.Status);
        Assert.False(vm.Segments[2].HasIssues); // untranslated: in the report only
    }

    [Fact]
    public async Task FixedIssues_AreStruckThrough_UntilF8RunsAgain()
    {
        var (vm, _) = await OpenAsync();
        vm.Qa.RunCommand.Execute(null);

        vm.Segments[0].TargetText = "Másolj 3 fájlt.";
        vm.Segments[2].TargetText = "Nyisd meg a fájlt.";

        Assert.Equal([true, true, true], Entries(vm).Where(e => e.Segment == vm.Segments[0] || e.Segment == vm.Segments[2]).Select(e => e.IsFixed));
        Assert.DoesNotContain(Entries(vm).Where(e => e.Segment == vm.Segments[1]), e => e.IsFixed);
        Assert.Equal("7 issues in 5 segments, 3 fixed; 4 left. Press F8 to check again.", vm.Qa.Status);

        vm.Qa.RunCommand.Execute(null);
        Assert.Equal(4, Entries(vm).Count);
    }

    [Fact]
    public async Task Dismissing_StopsCounting_UntilTheTargetChanges()
    {
        var (vm, _) = await OpenAsync();
        var segment = vm.Segments[0];
        vm.Qa.RunCommand.Execute(null);
        var numbers = Entries(vm).Where(e => e.Kind == QaIssueKind.Numbers).ToList();

        foreach (var entry in numbers)
            vm.Qa.ToggleDismissedCommand.Execute(entry);

        Assert.All(numbers, e => Assert.True(e.IsDismissed));
        Assert.False(segment.HasIssues);
        Assert.Equal(2, segment.AllIssues.Count);
        Assert.Equal("7 issues in 5 segments, 2 dismissed; 5 left. Press F8 to check again.", vm.Qa.Status);
        // From the top, Ctrl+Shift+Down has to pass over u1.
        vm.SelectedSegment = null;
        vm.NextSegmentWithIssuesCommand.Execute(null);
        Assert.Same(vm.Segments[1], vm.SelectedSegment); // u2's missing code, not u1

        // Restoring one brings it back; any edit to the target brings back the rest.
        vm.Qa.ToggleDismissedCommand.Execute(numbers[0]);
        Assert.Single(segment.Issues);
        segment.TargetText = "Másolj 4 fájlt!";
        Assert.Equal(3, segment.Issues.Count); // both numbers, and now the end punctuation
        Assert.All(numbers, e => Assert.False(e.IsDismissed));
    }

    [Fact]
    public async Task OnlyQualityIssues_CanBeDismissed()
    {
        var (vm, _) = await OpenAsync();
        vm.Qa.RunCommand.Execute(null);
        var segment = vm.Segments[1];

        Assert.False(vm.Qa.ToggleDismissedCommand.CanExecute(Entries(vm).Single(e => e.Kind is null)));
        Assert.False(segment.DismissCommand.CanExecute(segment.Issues.Single(i => i.Code is not null)));

        // The badge's menu lists only the quality issue, and nothing once that's dismissed.
        var dismissable = Assert.Single(segment.DismissableIssues);
        Assert.NotNull(dismissable.Qa);
        segment.DismissCommand.Execute(dismissable);
        Assert.Equal([true], segment.Issues.Select(i => i.Code is not null));
        Assert.False(segment.HasDismissableIssues);
    }

    [Fact]
    public async Task OpeningAnEntry_SelectsItsSegment_AndAsksForTheEditor()
    {
        var (vm, _) = await OpenAsync();
        vm.Qa.RunCommand.Execute(null);
        var asked = 0;
        vm.Qa.EditorRequested += (_, _) => asked++;

        vm.Qa.OpenCommand.Execute(Entries(vm).First(e => e.Segment == vm.Segments[4]));

        Assert.Same(vm.Segments[4], vm.SelectedSegment);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task SwitchingChecksOff_ChecksAgain_AndIsRemembered()
    {
        var settings = new MemorySettings { Saved = new AppSettings("/work/project.sctm") };
        var (vm, dialogs) = await OpenAsync(settings);
        vm.Qa.RunCommand.Execute(null);
        dialogs.EditQaSettings = s =>
        {
            s.Choices.Single(c => c.Kind == QaIssueKind.Numbers).IsEnabled = false;
            s.Choices.Single(c => c.Kind == QaIssueKind.Untranslated).IsEnabled = false;
            return true;
        };

        await vm.Qa.SettingsCommand.ExecuteAsync(null);

        Assert.False(vm.Segments[0].HasIssues);
        Assert.DoesNotContain(Entries(vm), e => e.Kind is QaIssueKind.Numbers or QaIssueKind.Untranslated); // the report ran again
        Assert.Equal("/work/project.sctm", settings.Saved.TranslationMemoryPath); // the other settings stay
        Assert.Equal(["Numbers", "Untranslated"], settings.Saved.DisabledQaChecks);

        // Next time, the same checks are off; and remembering a TM keeps them.
        var next = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null), settings);
        Assert.False(next.QaOptions.IsEnabled(QaIssueKind.Numbers));
        Assert.True(next.QaOptions.IsEnabled(QaIssueKind.Spaces));
        await next.CloseTranslationMemoryCommand.ExecuteAsync(null);
        Assert.Equal(["Numbers", "Untranslated"], settings.Saved.DisabledQaChecks);
    }

    [Fact]
    public async Task CancellingTheSettings_ChangesNothing()
    {
        var settings = new MemorySettings();
        var (vm, dialogs) = await OpenAsync(settings);
        dialogs.EditQaSettings = s =>
        {
            s.Choices[0].IsEnabled = false;
            return false;
        };

        await vm.Qa.SettingsCommand.ExecuteAsync(null);

        Assert.Same(QaOptions.Default, vm.QaOptions);
        Assert.Null(settings.Saved.DisabledQaChecks);
    }

    // The same number missing twice gives two identical issues; fixing one is one fixed, not none.
    [Fact]
    public async Task IdenticalIssues_AreFixedOneByOne()
    {
        var vm = await OpenAsync("""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Pages 5 or 5.</source><target>Oldalak.</target></segment></unit>
              </file>
            </xliff>
            """);
        vm.Qa.RunCommand.Execute(null);
        Assert.Equal(["u1: The number 5 isn't in the target.", "u1: The number 5 isn't in the target."], Entries(vm).Select(Text));

        vm.Segments[0].TargetText = "Oldalak: 5.";

        Assert.Equal([false, true], Entries(vm).Select(e => e.IsFixed));
        Assert.Equal("2 issues in 1 segments, 1 fixed; 1 left. Press F8 to check again.", vm.Qa.Status);
    }

    // Decision 4: the dismissal lasts while this target stays, whatever other segments do to the message's count.
    [Fact]
    public async Task ADismissedInconsistency_StaysDismissed_WhenAnotherSegmentChangesItsCount()
    {
        var vm = await OpenAsync("""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Save.</source><target>Mentés.</target></segment></unit>
                <unit id="u2"><segment><source>Save.</source><target>Mentsd.</target></segment></unit>
                <unit id="u3"><segment><source>Save.</source></segment></unit>
              </file>
            </xliff>
            """);
        vm.Qa.RunCommand.Execute(null);
        vm.Qa.ToggleDismissedCommand.Execute(Entries(vm).Single(e => e.Kind == QaIssueKind.InconsistentTranslation && e.Segment == vm.Segments[0]));

        vm.Segments[2].TargetText = "Ments.";
        vm.Qa.RunCommand.Execute(null);

        var entry = Entries(vm).Single(e => e.Kind == QaIssueKind.InconsistentTranslation && e.Segment == vm.Segments[0]);
        Assert.Equal("“Save.” is translated 3 ways in this document.", entry.Message);
        Assert.True(entry.IsDismissed);
    }

    // An inconsistency is fixed for the entry whose own target changed (whether that resolved it needs F8 again).
    [Fact]
    public async Task AnInconsistentTranslation_IsFixed_OnceItsOwnTargetChanges()
    {
        var (vm, _) = await OpenAsync();
        vm.Qa.RunCommand.Execute(null);

        vm.Segments[4].TargetText = "Mentés.";

        var inconsistent = Entries(vm).Where(e => e.Kind == QaIssueKind.InconsistentTranslation).ToList();
        Assert.Equal([vm.Segments[3], vm.Segments[4]], inconsistent.Select(e => e.Segment));
        Assert.Equal([false, true], inconsistent.Select(e => e.IsFixed));
    }

    [Fact]
    public async Task ChangingTheSettings_BeforeF8_DoesntRunTheReport_ButAfterAnEmptyOneDoes()
    {
        var (vm, dialogs) = await OpenAsync();
        dialogs.EditQaSettings = _ => true;

        await vm.Qa.SettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.PaneTab);
        Assert.Empty(vm.Qa.Rows);
        Assert.Equal("Press F8 to check the whole document.", vm.Qa.Status);

        // A report that found nothing ran all the same, so it runs again.
        var clean = await OpenAsync("""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1"><unit id="u1"><segment><source>Save.</source><target>Mentés.</target></segment></unit></file>
            </xliff>
            """, dialogs);
        clean.Qa.RunCommand.Execute(null);
        Assert.Equal("No issues found.", clean.Qa.Status);
        clean.PaneTab = 0;
        await clean.Qa.SettingsCommand.ExecuteAsync(null);
        Assert.Equal(2, clean.PaneTab);
        Assert.Equal("No issues found.", clean.Qa.Status);
    }

    [Fact]
    public async Task OpeningAnotherDocument_ForgetsTheReport()
    {
        var (vm, _) = await OpenAsync();
        vm.Qa.RunCommand.Execute(null);

        await vm.LoadAsync(_files.Write("other.xlf", Document));

        Assert.Empty(vm.Qa.Rows);
        Assert.Equal("Press F8 to check the whole document.", vm.Qa.Status);
    }

    // ---------------------------------------------------------------- the real window and dialog

    [AvaloniaFact]
    public async Task F8InTheWindow_ShowsTheReport_AClickOpensTheSegment_DismissGreysItOut()
    {
        var path = _files.Write("qa.xlf", Document);
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(path));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.LoadAsync(path);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.FindControl<TabControl>("PaneTabs")!.SelectedIndex);
        Assert.Equal("7 issues in 5 segments.", window.FindControl<TextBlock>("QaStatus")!.Text);

        // A click on u2's entry selects u2 and gives its editor the keyboard.
        var row = Rows(window).Single(r => r.DataContext is QaReportEntry e && e.Segment == vm.Segments[1]);
        Click(window, row);
        await WaitFor(() => window.GetVisualDescendants().OfType<TargetEditor>().Any(t => t.IsEffectivelyVisible && t.Editor.TextArea.IsKeyboardFocusWithin));
        Assert.Same(vm.Segments[1], vm.SelectedSegment);

        // Dismiss greys the entry and turns into Restore; u1's badge goes. (The list only lays out the rows it shows.)
        var list = window.FindControl<ListBox>("QaList")!;
        list.ScrollIntoView(vm.Qa.Rows.First(r => r is QaReportEntry { Kind: QaIssueKind.Numbers }));
        var numbers = Rows(window).First(r => r.DataContext is QaReportEntry { Kind: QaIssueKind.Numbers });
        var button = numbers.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("dismissIssue"));
        Assert.Equal("Dismiss", button.Content);
        Click(window, button); // a real click: raising Click alone doesn't run a button's command
        Assert.Equal("Restore", button.Content);
        Assert.Contains("dismissed", numbers.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("qaEntry")).Classes);
        Assert.Single(vm.Segments[0].Issues);
        window.Close();
    }

    // F8 gives the report the keyboard, so Up/Down and Enter work through it.
    [AvaloniaFact]
    public async Task F8InTheWindow_FocusesTheFirstEntry_AndEnterOpensIt()
    {
        var (window, vm) = await ShowAsync(Document);

        window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
        var list = window.FindControl<ListBox>("QaList")!;
        await WaitFor(() => list.IsKeyboardFocusWithin);
        var first = Assert.IsType<QaReportEntry>(list.SelectedItem);
        Assert.Same(vm.Segments[1], first.Segment); // u2's missing code heads the report

        // Down passes over the next check's heading to its first entry.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Same(vm.Segments[0], Assert.IsType<QaReportEntry>(list.SelectedItem).Segment);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await WaitFor(() => window.GetVisualDescendants().OfType<TargetEditor>().Any(t => t.IsEffectivelyVisible && t.Editor.TextArea.IsKeyboardFocusWithin));
        Assert.Same(((QaReportEntry)list.SelectedItem!).Segment, vm.SelectedSegment);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AClickBesideTheEntries_OpensNothing()
    {
        var (window, vm) = await ShowAsync("""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Copy 3 files.</source><target>Másolj 3 fájlt</target></segment></unit>
                <unit id="u2"><segment><source>Save.</source><target>Mentés.</target></segment></unit>
              </file>
            </xliff>
            """);
        vm.Qa.RunCommand.Execute(null);
        Click(window, Rows(window).Single(r => r.DataContext is QaReportEntry));
        Assert.Same(vm.Segments[0], vm.SelectedSegment);
        vm.SelectedSegment = vm.Segments[1];

        var list = window.FindControl<ListBox>("QaList")!;
        var below = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 4), window)!.Value;
        window.MouseDown(below, MouseButton.Left);
        window.MouseUp(below, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("OkButton", true)]
    [InlineData("CancelButton", false)]
    public async Task TheSettingsDialog_ListsEveryCheck_AndReturnsTheChoice(string buttonName, bool expected)
    {
        var owner = new Window();
        owner.Show();
        var settings = new QaSettingsViewModel(new QaOptions { Disabled = new HashSet<QaIssueKind> { QaIssueKind.Spaces } });
        var answer = new AvaloniaDialogService(owner).EditQaSettingsAsync(settings);
        Dispatcher.UIThread.RunJobs();
        var dialog = owner.OwnedWindows.OfType<QaSettingsDialog>().Single();

        var boxes = dialog.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(QaChecks.All.Count, boxes.Count);
        var spaces = boxes.Single(b => (string?)b.Content == "Spaces");
        Assert.False(spaces.IsChecked);
        spaces.IsChecked = true;
        dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => answer.IsCompleted);

        Assert.Equal(expected, await answer);
        Assert.True(settings.ToOptions().IsEnabled(QaIssueKind.Spaces));
        owner.Close();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenAsync(ISettingsStore? settings = null)
    {
        var dialogs = new FakeDialogs(null);
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs, settings);
        await vm.LoadAsync(_files.Write("qa.xlf", Document));
        Assert.Null(vm.ErrorMessage);
        return (vm, dialogs);
    }

    private async Task<MainWindowViewModel> OpenAsync(string document, FakeDialogs? dialogs = null)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs ?? new FakeDialogs(null));
        await vm.LoadAsync(_files.Write($"doc{Guid.NewGuid():N}.xlf", document));
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    private async Task<(MainWindow Window, MainWindowViewModel ViewModel)> ShowAsync(string document)
    {
        var path = _files.Write("window.xlf", document);
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(path));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.LoadAsync(path);
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static List<QaReportEntry> Entries(MainWindowViewModel vm) => [.. vm.Qa.Rows.OfType<QaReportEntry>()];

    private static string Text(QaReportRow row) => row switch
    {
        QaReportHeader header => header.Text,
        QaReportEntry entry => $"{entry.Segment.Label}: {entry.Message}",
        _ => throw new InvalidOperationException(),
    };

    private static List<ListBoxItem> Rows(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.FindControl<ListBox>("QaList")!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(8, 8), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The window did not reach the expected state.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Saved { get; set; } = new();

        public AppSettings Load() => Saved;

        public void Save(AppSettings settings) => Saved = settings;
    }
}
