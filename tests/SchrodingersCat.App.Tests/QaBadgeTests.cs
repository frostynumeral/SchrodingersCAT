using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

/// <summary>Step 3 of <c>docs/design/qa-checks.md</c>: quality issues on the segments' badges, and in Ctrl+Shift+Down.</summary>
public sealed class QaBadgeTests : IDisposable
{
    /// <summary>A number mistyped (a warning), a missing full stop (info), an untranslated segment, and a clean one.</summary>
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Save the file.</source><target>Mentsd a fájlt</target></segment></unit>
            <unit id="u2"><segment><source>Copy 3 files.</source><target>Másolj 4 fájlt.</target></segment></unit>
            <unit id="u3"><segment><source>Open the file.</source></segment></unit>
            <unit id="u4"><segment><source>Close <pc id="1">it</pc>.</source><target>Zárd <pc id="1">be</pc>.</target></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task QualityIssues_ShowWithTheCodeIssues_ButOutlineNoChips()
    {
        var vm = await OpenAsync();
        var (missingFullStop, wrongNumber, untranslated, clean) = (vm.Segments[0], vm.Segments[1], vm.Segments[2], vm.Segments[3]);

        Assert.Equal(2, wrongNumber.Issues.Count);
        Assert.All(wrongNumber.Issues, issue =>
        {
            Assert.Equal((QaIssueKind.Numbers, IssueSeverity.Warning), (issue.Qa!.Kind, issue.Severity));
            Assert.Null(issue.Code);
        });
        Assert.True(wrongNumber.HasWarnings);
        Assert.False(wrongNumber.HasErrors);
        Assert.Equal("Warning: The number 3 isn't in the target.\nWarning: The target has the number 4, which the source doesn't.",
            string.Join("\n", wrongNumber.IssueSummary!.Split('\n').Order(StringComparer.Ordinal)));

        Assert.Equal([IssueSeverity.Info], missingFullStop.Issues.Select(i => i.Severity));
        Assert.False(missingFullStop.HasWarnings);
        Assert.False(untranslated.HasIssues); // only the report (F8) lists untranslated segments
        Assert.False(clean.HasIssues);
        Assert.Empty(clean.TargetProblemCodes);
    }

    // Content marked translate="no" by its group is meant to stay as it is: a copied source is no warning there.
    [Fact]
    public async Task AUnitInAGroupNotToTranslate_HasNoQualityBadge()
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null));
        await vm.LoadAsync(_files.Write("brand.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <group id="g1" translate="no">
                  <unit id="u1"><segment><source>Acme Cloud Server</source><target>Acme Cloud Server</target></segment></unit>
                </group>
                <unit id="u2"><segment><source>Acme Cloud Server</source><target>Acme Cloud Server</target></segment></unit>
              </file>
            </xliff>
            """));

        Assert.Equal([false, true], vm.Segments.Select(s => s.HasWarnings));
    }

    [Fact]
    public async Task AnEdit_ClearsTheQualityIssue()
    {
        var vm = await OpenAsync();
        var segment = vm.Segments[1];
        var changed = new List<string?>();
        segment.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        segment.TargetText = "Másolj 3 fájlt.";

        Assert.False(segment.HasIssues);
        Assert.Contains(nameof(SegmentViewModel.Issues), changed);
        Assert.Contains(nameof(SegmentViewModel.HasWarnings), changed);
    }

    [Fact]
    public async Task CodeIssues_ComeFirst()
    {
        var vm = await OpenAsync();
        var segment = vm.Segments[3];

        segment.TargetText = "Zárd be"; // the code gone (a warning), and the full stop (info)

        Assert.Equal([true, false], segment.Issues.Select(i => i.Code is not null));
    }

    [Fact]
    public async Task CtrlShiftDown_StopsAtQualityWarnings_NotAtInfo()
    {
        var vm = await OpenAsync();
        vm.SelectedSegment = vm.Segments[0];

        vm.NextSegmentWithIssuesCommand.Execute(null);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);

        vm.NextSegmentWithIssuesCommand.Execute(null);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        Assert.Equal("No segments with errors or warnings below this one.", vm.Segments[1].EditStatus);
    }

    [AvaloniaFact]
    public async Task TheBadge_ShowsAQualityWarning_AndCtrlShiftDownGoesThere()
    {
        var path = _files.Write("qa.xlf", Document);
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(path));
        var window = new Views.MainWindow { DataContext = vm };
        window.Show();
        await vm.LoadAsync(path);
        Dispatcher.UIThread.RunJobs();

        var badges = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("issueBadge")).ToList();
        Assert.Equal([true, true, false, false], badges.Select(b => b.IsVisible));
        Assert.Contains("warning", badges[1].Classes);
        Assert.DoesNotContain("warning", badges[0].Classes); // info only
        Assert.Contains("The number 3 isn't in the target.", (string)ToolTip.GetTip(badges[1])!, StringComparison.Ordinal);

        vm.SelectedSegment = vm.Segments[0];
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        window.Close();
    }

    private async Task<MainWindowViewModel> OpenAsync()
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null));
        await vm.LoadAsync(_files.Write("qa.xlf", Document));
        Assert.Null(vm.ErrorMessage);
        return vm;
    }
}
