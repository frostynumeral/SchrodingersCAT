using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

public class InlineCodeCheckerTests : Xliff20SerializerTestBase
{
    [Fact]
    public async Task TargetMatchingTheSource_HasNoIssues()
    {
        var issues = await CheckAsync("""
            <originalData><data id="d1">&lt;b&gt;</data><data id="d2">&lt;/b&gt;</data><data id="d3">{0}</data></originalData>
            <segment><source>Press <pc id="1" dataRefStart="d1" dataRefEnd="d2">OK</pc> for <ph id="2" dataRef="d3"/> files.</source>
                     <target>Nyomd meg az <pc id="1" dataRefStart="d1" dataRefEnd="d2">OK</pc> gombot <ph id="2" dataRef="d3"/> fájlhoz.</target></segment>
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task MissingDeletableCode_IsAWarning()
    {
        var issue = Assert.Single(await CheckAsync("""
            <segment><source>Hello <ph id="1"/>world</source><target>Szia világ</target></segment>
            """));

        Assert.Equal((InlineCodeIssueKind.MissingCode, IssueSeverity.Warning, "1"), (issue.Kind, issue.Severity, issue.CodeId));
    }

    [Fact]
    public async Task MissingProtectedCode_IsAnError()
    {
        var issue = Assert.Single(await CheckAsync("""
            <segment><source>Can't open <ph id="1" canDelete="no" canCopy="no" disp="%s"/>.</source><target>Nem nyitható meg.</target></segment>
            """));

        Assert.Equal((InlineCodeIssueKind.MissingProtectedCode, IssueSeverity.Error), (issue.Kind, issue.Severity));
        Assert.Contains("(%s)", issue.Message);
    }

    [Fact]
    public async Task MissingPair_IsReportedOnce()
    {
        var issue = Assert.Single(await CheckAsync("""
            <segment><source><pc id="1">Bold</pc> text</source><target>Félkövér szöveg</target></segment>
            """));

        Assert.Equal(InlineCodeIssueKind.MissingCode, issue.Kind);
    }

    [Fact]
    public async Task UnknownCode_IsAnError()
    {
        var issue = Assert.Single(await CheckAsync("""
            <segment><source>Hello</source><target>Szia<ph id="9"/></target></segment>
            """));

        Assert.Equal((InlineCodeIssueKind.UnknownCode, IssueSeverity.Error, "9"), (issue.Kind, issue.Severity, issue.CodeId));
    }

    [Fact]
    public async Task CodeOfADifferentKindThanInTheSource_IsUnknown()
    {
        var issues = await CheckAsync("""
            <segment><source>Hello<ph id="1"/></source><target>Szia<sc id="1" isolated="yes"/></target></segment>
            """);

        Assert.Contains(issues, i => i.Kind == InlineCodeIssueKind.UnknownCode && i.Message.Contains("different kind", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyWithTheSameDataRef_IsAllowed()
    {
        // XLIFF spec 4.7.2.4.1: the copy has a new id and reuses the base code's original data.
        var issues = await CheckAsync("""
            <originalData><data id="d1">&lt;b&gt;</data><data id="d2">&lt;/b&gt;</data></originalData>
            <segment><source>Äter <pc id="1" dataRefStart="d1" dataRefEnd="d2">katter möss</pc>?</source>
                     <target>Do <pc id="1" dataRefStart="d1" dataRefEnd="d2">cats</pc> eat <pc id="2" dataRefStart="d1" dataRefEnd="d2">mice</pc>?</target></segment>
            """);

        Assert.DoesNotContain(issues, i => i.Severity >= IssueSeverity.Warning);
    }

    [Fact]
    public async Task CopyWithCopyOf_IsAllowed()
    {
        // XLIFF spec 4.7.2.4.1: without original data, the copy names its base code with copyOf.
        var issues = await CheckAsync("""
            <segment><source>Esznek <pc id="1">a magyarok svéd húsgombócot</pc>?</source>
                     <target>Do <pc id="1">Hungarians</pc> eat <pc id="2" copyOf="1">Swedish meatballs</pc>?</target></segment>
            """);

        Assert.DoesNotContain(issues, i => i.Severity >= IssueSeverity.Warning);
    }

    [Theory]
    [InlineData("""<ph id="2" dataRef="d1"/>""")]
    [InlineData("""<ph id="2" copyOf="1"/>""")]
    public async Task CopyOfANonCopyableCode_IsAnError(string copy)
    {
        var issue = Assert.Single(await CheckAsync($"""
            <originalData><data id="d1">%s</data></originalData>
            <segment><source>File <ph id="1" dataRef="d1" canCopy="no"/></source><target>Fájl <ph id="1" dataRef="d1"/> {copy}</target></segment>
            """));

        Assert.Equal((InlineCodeIssueKind.CopyNotAllowed, IssueSeverity.Error, "2"), (issue.Kind, issue.Severity, issue.CodeId));
    }

    // Unpaired targets can't be read from XLIFF (the reader rejects them), but they happen while editing.
    [Fact]
    public void OneHalfOfAPair_IsUnpaired()
    {
        var unit = Unit(("s1", [Open("1"), Text("Bold"), Close("1"), Text(" text")], [Open("1"), Text("Félkövér szöveg")]));

        var issue = Assert.Single(InlineCodeChecker.Check(unit));

        Assert.Equal((InlineCodeIssueKind.Unpaired, IssueSeverity.Error), (issue.Kind, issue.Severity));
    }

    [Fact]
    public void ClosingHalfBeforeItsOpening_IsUnpaired()
    {
        var unit = Unit(("s1", [Open("1"), Text("a"), Close("1")], [Close("1"), Text("a"), Open("1")]));

        var issues = InlineCodeChecker.Check(unit);

        Assert.Equal(2, issues.Count(i => i.Kind == InlineCodeIssueKind.Unpaired));
    }

    [Fact]
    public async Task PairSpanningSegments_IsFine()
    {
        var issues = await CheckAsync("""
            <segment id="s1"><source><sc id="1"/>First sentence. </source><target><sc id="1"/>Első mondat. </target></segment>
            <segment id="s2"><source>Second sentence.<ec startRef="1"/></source><target>Második mondat.<ec startRef="1"/></target></segment>
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public void PairSpanningSegments_WithTheSecondUntranslated_IsUnpairedButNotMissing()
    {
        // The writer requires every pair to close within the unit's target, so this can't be saved yet.
        var unit = Unit(
            ("s1", [Open("1"), Text("First sentence. ")], [Open("1"), Text("Első mondat. ")]),
            ("s2", [Text("Second sentence."), Close("1")], null));

        var issue = Assert.Single(InlineCodeChecker.Check(unit));

        Assert.Equal(InlineCodeIssueKind.Unpaired, issue.Kind);
        Assert.Equal("s1", issue.Segment.Id);
    }

    [Fact]
    public async Task UntranslatedSegments_AreNotChecked()
    {
        var issues = await CheckAsync("""
            <segment id="s1"><source>One <ph id="1"/></source></segment>
            <segment id="s2"><source>Two</source><target>Kettő</target></segment>
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task Ignorables_AreNotComparedWithTheSource()
    {
        var issues = await CheckAsync("""
            <segment id="s1"><source>One.</source><target>Egy.</target></segment>
            <ignorable><source> <ph id="1"/> </source><target> </target></ignorable>
            """);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task NonReorderableSequenceOutOfOrder_IsBroken()
    {
        // The spec's own example: two variables that may not swap places.
        var issues = await CheckAsync("""
            <originalData><data id="d1">%s</data><data id="d2">%d</data></originalData>
            <segment><source>Number of <ph id="1" dataRef="d1" canCopy="no" canDelete="no" canReorder="firstNo"/>: <ph id="2" dataRef="d2" canCopy="no" canDelete="no" canReorder="no"/>.</source>
                     <target><ph id="2" dataRef="d2"/> darab <ph id="1" dataRef="d1"/>.</target></segment>
            """);

        Assert.Contains(issues, i => i is { Kind: InlineCodeIssueKind.SequenceBroken, Severity: IssueSeverity.Error, CodeId: "1" });
    }

    [Fact]
    public async Task NonReorderableSequenceInOrder_IsFine()
    {
        var issues = await CheckAsync("""
            <originalData><data id="d1">%s</data><data id="d2">%d</data></originalData>
            <segment><source>Number of <ph id="1" dataRef="d1" canCopy="no" canDelete="no" canReorder="firstNo"/>: <ph id="2" dataRef="d2" canCopy="no" canDelete="no" canReorder="no"/>.</source>
                     <target><ph id="1" dataRef="d1"/> száma: <ph id="2" dataRef="d2"/>.</target></segment>
            """);

        Assert.Empty(issues);
    }

    // Decision 10: codes in a sequence count as non-deletable and non-copyable even when the file forgot to say so.
    [Fact]
    public async Task SequenceCodesWithoutTheRequiredAttributes_AreStillProtected()
    {
        var issues = await CheckAsync("""
            <originalData><data id="d1">%s</data><data id="d2">%d</data></originalData>
            <segment><source><ph id="1" dataRef="d1" canReorder="firstNo"/> and <ph id="2" dataRef="d2" canReorder="no"/></source>
                     <target><ph id="1" dataRef="d1"/> és <ph id="3" dataRef="d1"/></target></segment>
            """);

        Assert.Contains(issues, i => i is { Kind: InlineCodeIssueKind.MissingProtectedCode, CodeId: "2" });
        Assert.Contains(issues, i => i is { Kind: InlineCodeIssueKind.CopyNotAllowed, CodeId: "3" });
    }

    [Fact]
    public async Task ASingleFirstNoCode_IsLikeCanReorderYes()
    {
        // Spec: a non-reorderable sequence of one code is equivalent to canReorder="yes".
        var issues = await CheckAsync("""
            <segment><source><pc id="1" canReorder="firstNo">a</pc> b</source><target>b</target></segment>
            """);

        Assert.Equal(InlineCodeIssueKind.MissingCode, Assert.Single(issues).Kind);
    }

    [Fact]
    public async Task ReorderedCodes_AreInfo()
    {
        var issue = Assert.Single(await CheckAsync("""
            <segment><source><ph id="1"/> then <ph id="2"/></source><target><ph id="2"/> majd <ph id="1"/></target></segment>
            """));

        Assert.Equal((InlineCodeIssueKind.Reordered, IssueSeverity.Info), (issue.Kind, issue.Severity));
    }

    [Fact]
    public async Task CodeMovedToAnotherSegment_IsInfo()
    {
        var issues = await CheckAsync("""
            <segment id="s1"><source>One <ph id="1"/></source><target>Egy</target></segment>
            <segment id="s2"><source>Two</source><target>Kettő <ph id="1"/></target></segment>
            """);

        var issue = Assert.Single(issues);
        Assert.Equal((InlineCodeIssueKind.Reordered, IssueSeverity.Info, "s2"), (issue.Kind, issue.Severity, issue.Segment.Id));
    }

    [Fact]
    public async Task MissingAnnotation_IsInfo()
    {
        var issue = Assert.Single(await CheckAsync("""
            <segment><source>Buy <mrk id="m1" type="term">ACME</mrk></source><target>Vegyél ACME-t</target></segment>
            """));

        Assert.Equal((InlineCodeIssueKind.MissingAnnotation, IssueSeverity.Info, "m1"), (issue.Kind, issue.Severity, issue.CodeId));
        Assert.Contains("(term)", issue.Message);
    }

    [Fact]
    public async Task IssuesPointAtTheSegmentTheyAreIn()
    {
        var issues = await CheckAsync("""
            <segment id="s1"><source>One <ph id="1"/></source><target>Egy</target></segment>
            <segment id="s2"><source>Two</source><target>Kettő <ph id="9"/></target></segment>
            """);

        Assert.Equal("s1", issues.Single(i => i.Kind == InlineCodeIssueKind.MissingCode).Segment.Id);
        Assert.Equal("s2", issues.Single(i => i.Kind == InlineCodeIssueKind.UnknownCode).Segment.Id);
    }

    private async Task<IReadOnlyList<InlineCodeIssue>> CheckAsync(string unitContent) =>
        InlineCodeChecker.Check(await ReadUnitAsync(unitContent));

    private static TranslationUnit Unit(params (string Id, InlineElement[] Source, InlineElement[]? Target)[] segments)
    {
        var unit = new TranslationUnit { Id = "u1" };
        foreach (var (id, source, target) in segments)
        {
            unit.Segments.Add(new Segment
            {
                Id = id,
                Source = new InlineContent(source),
                Target = target is null ? null : new InlineContent(target),
            });
        }

        return unit;
    }

    private static InlineText Text(string text) => new(text);

    private static InlineCode Open(string id) => new(id, InlineCodeKind.Opening);

    private static InlineCode Close(string id) => new(id, InlineCodeKind.Closing);
}
