using System.Text;
using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.App.Tests;

public class TargetEditModelTests
{
    // Source: "Press ⟨1⟩OK⟨/1⟩ for ⟨2/⟩ files." with 2 = "{0}", a placeholder that may not be deleted or copied.
    private const string Sample = """
        <originalData><data id="d1">&lt;b&gt;</data><data id="d2">&lt;/b&gt;</data><data id="d3">{0}</data></originalData>
        <segment id="s1"><source>Press <pc id="1" dataRefStart="d1" dataRefEnd="d2">OK</pc> for <ph id="2" dataRef="d3" disp="{0}" canDelete="no" canCopy="no"/> files.</source><target></target></segment>
        """;

    // ---------------------------------------------------------------- basics

    [Fact]
    public async Task StartsWithTheTarget_AndTheCaretAtTheEnd()
    {
        var model = await EditAsync("""<segment><source>a<ph id="1"/></source><target>x<ph id="1"/>y</target></segment>""");

        Assert.Equal("x⟨1/⟩y|", Show(model));
        Assert.Equal(3, model.Length); // the chip is one position
        Assert.Equal([typeof(TextToken), typeof(CodeToken), typeof(TextToken)], model.Tokens.Select(t => t.GetType()));
    }

    [Fact]
    public async Task Ignorables_CantBeEdited()
    {
        var unit = await ReadUnitAsync("""<segment><source>a</source></segment><ignorable><source> </source></ignorable>""");

        Assert.Throws<ArgumentException>(() => new TargetEditModel(unit, unit.Segments[1]));
        Assert.Throws<ArgumentException>(() => new TargetEditModel(unit, new Segment { Source = InlineContent.Empty }));
    }

    [Fact]
    public async Task ToContent_IsTheTargetUnchanged()
    {
        var unit = await ReadUnitAsync("""<segment><source><pc id="1">a</pc><mrk id="m1" type="term">b</mrk></source><target><pc id="1">x</pc> <mrk id="m1" type="term">y</mrk>z</target></segment>""");
        var target = unit.Segments[0].Target!;

        Assert.Equal(target, new TargetEditModel(unit, unit.Segments[0]).ToContent());
    }

    [Fact]
    public async Task MoveCaret_ClampsAndNeverSplitsASurrogatePair()
    {
        var model = await EditAsync("""<segment><source>a</source><target>a😀b</target></segment>""");

        model.MoveCaret(99);
        Assert.Equal(model.Length, model.Caret);
        model.MoveCaret(-5);
        Assert.Equal(0, model.Caret);
        model.MoveCaret(2); // between the two halves of 😀
        Assert.Equal(3, model.Caret);
    }

    // ---------------------------------------------------------------- typing

    [Fact]
    public async Task InsertText_TypesAtTheCaretAndWritesTheTarget()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);

        model.InsertText("Nyomd meg");

        Assert.Equal("Nyomd meg|", Show(model));
        Assert.Equal("Nyomd meg", unit.Segments[0].Target!.ToPlainText());
    }

    [Fact]
    public async Task InsertText_ReplacesTheSelection()
    {
        var model = await EditAsync("""<segment><source>a</source><target>Hello world</target></segment>""");
        model.Select(6, 11);

        model.InsertText("there");

        Assert.Equal("Hello there|", Show(model));
    }

    [Fact]
    public async Task InsertText_OverASelection_KeepsProtectedCodes()
    {
        var model = await EditAsync(Sample);
        model.InsertCode("2");
        model.InsertText(" fájl");
        model.Select(0, model.Length);

        var result = model.InsertText("Fájlok: ");

        Assert.Equal("Fájlok: |⟨2/⟩", Show(model));
        Assert.True(result.Applied);
        Assert.Contains("Kept 1 code", result.Message);
    }

    [Fact]
    public async Task ClearingTheTarget_MeansNotTranslated_UnlessItWasIntentionallyEmpty()
    {
        var (model, unit) = await EditWithUnitAsync("""<segment><source>a</source><target>x</target></segment>""");
        model.Delete(DeleteDirection.Backward);
        Assert.Null(unit.Segments[0].Target);

        var (emptyModel, emptyUnit) = await EditWithUnitAsync("""<segment><source>a</source><target></target></segment>""");
        emptyModel.InsertText("x");
        emptyModel.Delete(DeleteDirection.Backward);
        Assert.Equal(InlineContent.Empty, emptyUnit.Segments[0].Target);
    }

    // ---------------------------------------------------------------- inserting codes

    [Fact]
    public async Task InsertCode_Pair_PutsTheCaretBetweenTheHalves()
    {
        var model = await EditAsync(Sample);
        model.InsertText("Nyomd meg az ");

        model.InsertCode("1");
        model.InsertText("OK");

        Assert.Equal("Nyomd meg az ⟨1⟩OK|⟨/1⟩", Show(model));
    }

    [Fact]
    public async Task InsertCode_Pair_WrapsTheSelection()
    {
        var model = await EditAsync("""<segment><source><pc id="1">OK</pc></source><target>Nyomd meg az OK gombot</target></segment>""");
        model.Select(13, 15);

        model.InsertCode("1");

        Assert.Equal("Nyomd meg az ⟨1⟩OK⟨/1⟩| gombot", Show(model));
    }

    [Fact]
    public async Task InsertCode_HalfOfAPairAlreadyThere_InsertsOnlyTheMissingHalf()
    {
        var model = await EditAsync("""<segment><source><sc id="1"/>a<ec startRef="1"/></source><target>x</target></segment>""");
        model.InsertCode("1"); // both halves
        model.MoveCaret(model.Length);
        model.Delete(DeleteDirection.Backward); // the closing half
        model.MoveCaret(model.Length);

        model.InsertCode("1");

        Assert.Equal("x⟨1⟩⟨/1⟩|", Show(model));
    }

    [Fact]
    public async Task InsertCode_AlreadyInTheTarget_InsertsACopyThatReusesTheOriginalData()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.InsertCode("1");
        model.MoveCaret(model.Length);

        var result = model.InsertCode("1");

        Assert.True(result.Applied);
        var copy = model.ToContent().OfType<InlineCode>().Where(c => c.Id != "1").ToList();
        Assert.Equal([InlineCodeKind.Opening, InlineCodeKind.Closing], copy.Select(c => c.Kind));
        Assert.Single(copy.Select(c => c.Id).Distinct());
        Assert.Equal(["d1", "d2"], copy.Select(c => c.DataRef));
        Assert.All(copy, c => Assert.Null(c.CopyOf)); // the original data identifies the base code
        Assert.DoesNotContain(InlineCodeChecker.Check(unit), i => i.Kind is InlineCodeIssueKind.UnknownCode or InlineCodeIssueKind.CopyNotAllowed);
    }

    [Fact]
    public async Task InsertCode_CopyOfACodeWithoutOriginalData_UsesCopyOf()
    {
        var model = await EditAsync("""<segment><source><pc id="1">a</pc></source><target><pc id="1">x</pc></target></segment>""");

        model.InsertCode("1");

        var copy = model.ToContent().OfType<InlineCode>().First(c => c.Id != "1");
        Assert.Equal("1", copy.CopyOf);
    }

    [Fact]
    public async Task InsertCode_NewIdIsUnusedInTheUnit()
    {
        var model = await EditAsync("""<segment id="1"><source><ph id="2"/><ph id="3"/></source><target><ph id="2"/></target></segment>""");

        model.InsertCode("2");

        Assert.Contains(model.ToContent().OfType<InlineCode>(), c => c.Id == "4" && c.CopyOf == "2");
    }

    [Fact]
    public async Task InsertCode_NonCopyable_IsRefused()
    {
        var model = await EditAsync(Sample);
        model.InsertCode("2");
        var before = model.ToContent();

        var result = model.InsertCode("2");

        Assert.True(result.IsRefused);
        Assert.Contains("may not be copied", result.Message);
        Assert.Equal(before, model.ToContent());
    }

    // Decision 10: codes in a non-reorderable sequence are non-copyable even when the file forgot to say so.
    [Fact]
    public async Task InsertCode_CodeInANonReorderableSequence_IsNotCopyable()
    {
        var model = await EditAsync("""<segment><source><ph id="1" canReorder="firstNo"/><ph id="2" canReorder="no"/></source><target><ph id="1"/><ph id="2"/></target></segment>""");

        Assert.True(model.InsertCode("1").IsRefused);
    }

    [Fact]
    public async Task InsertCode_UnknownId_IsRefused()
    {
        var model = await EditAsync(Sample);

        Assert.Contains("no code '9'", model.InsertCode("9").Message);
    }

    [Fact]
    public async Task InsertNextMissingCode_GoesInSourceOrder()
    {
        var model = await EditAsync(Sample);

        model.InsertNextMissingCode();
        model.MoveCaret(model.Length);
        model.InsertNextMissingCode();

        Assert.Equal("⟨1⟩⟨/1⟩⟨2/⟩|", Show(model));
        var result = model.InsertNextMissingCode();
        Assert.True(result.IsRefused);
        Assert.Contains("already in the target", result.Message);
    }

    [Fact]
    public async Task InsertNextMissingCode_WrapsTheSelection()
    {
        var model = await EditAsync("""<segment><source><pc id="1">OK</pc></source><target>az OK gomb</target></segment>""");
        model.Select(3, 5);

        model.InsertNextMissingCode();

        Assert.Equal("az ⟨1⟩OK⟨/1⟩| gomb", Show(model));
    }

    [Fact]
    public async Task InsertingAllMissingCodes_LeavesNothingMissingForTheChecker()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        while (model.InsertNextMissingCode().Applied)
            model.MoveCaret(model.Length);

        Assert.DoesNotContain(InlineCodeChecker.Check(unit), i => i.Severity >= IssueSeverity.Warning);
    }

    // ---------------------------------------------------------------- deleting

    [Fact]
    public async Task Delete_RemovesOneCharacterInEitherDirection()
    {
        var model = await EditAsync("""<segment><source>a</source><target>abc</target></segment>""");
        model.MoveCaret(1);

        model.Delete(DeleteDirection.Forward);
        Assert.Equal("a|c", Show(model));

        model.Delete(DeleteDirection.Backward);
        Assert.Equal("|c", Show(model));
        Assert.Equal(EditResult.Unchanged, model.Delete(DeleteDirection.Backward));
    }

    [Fact]
    public async Task Delete_RemovesASurrogatePairTogether()
    {
        var model = await EditAsync("""<segment><source>a</source><target>a😀</target></segment>""");

        model.Delete(DeleteDirection.Backward);

        Assert.Equal("a|", Show(model));
    }

    [Fact]
    public async Task Delete_RemovesAWholeChip()
    {
        var model = await EditAsync("""<segment><source>a<ph id="1"/></source><target>a<ph id="1"/>b</target></segment>""");
        model.MoveCaret(2);

        model.Delete(DeleteDirection.Backward);

        Assert.Equal("a|b", Show(model));
    }

    [Fact]
    public async Task Delete_ProtectedCode_IsRefused()
    {
        var model = await EditAsync(Sample);
        model.InsertCode("2");

        var result = model.Delete(DeleteDirection.Backward);

        Assert.True(result.IsRefused);
        Assert.Equal("Code '2' ({0}) may not be deleted.", result.Message);
        Assert.Equal("⟨2/⟩|", Show(model));
    }

    [Fact]
    public async Task Delete_OneHalfOfAPair_LeavesTheOther()
    {
        var model = await EditAsync("""<segment><source><pc id="1">a</pc></source><target><pc id="1">x</pc></target></segment>""");
        model.MoveCaret(1); // after the opening half

        model.Delete(DeleteDirection.Backward);

        Assert.Equal("|x⟨/1⟩", Show(model));
    }

    [Fact]
    public async Task Delete_Selection_KeepsProtectedCodes()
    {
        var model = await EditAsync(Sample);
        model.InsertText("a");
        model.InsertCode("2");
        model.InsertText("b");
        model.InsertCode("1");
        model.Select(0, model.Length);

        var result = model.Delete(DeleteDirection.Backward);

        Assert.Equal("|⟨2/⟩", Show(model));
        Assert.Contains("Kept 1 code", result.Message);
    }

    [Fact]
    public async Task Delete_SelectionOfOnlyProtectedCodes_IsRefused()
    {
        var model = await EditAsync(Sample);
        model.InsertCode("2");
        model.Select(0, 1);

        Assert.True(model.Delete(DeleteDirection.Forward).IsRefused);
        Assert.Equal(1, model.Length);
    }

    [Fact]
    public async Task Delete_Annotations_AreAlwaysDeletable()
    {
        var model = await EditAsync("""<segment><source><mrk id="m1" type="term">a</mrk></source><target><mrk id="m1" type="term">x</mrk></target></segment>""");

        model.Delete(DeleteDirection.Backward);

        Assert.Equal("⟦m1⟧x|", Show(model));
    }

    // ---------------------------------------------------------------- copy source

    [Fact]
    public async Task CopySource_ReplacesTheTargetWithTheSource()
    {
        var model = await EditAsync(Sample);
        model.InsertText("junk");

        model.CopySource();

        Assert.Equal("Press ⟨1⟩OK⟨/1⟩ for ⟨2/⟩ files.|", Show(model));
    }

    [Fact]
    public async Task CopySource_LeavesOutCodesUsedInAnotherSegment()
    {
        var unit = await ReadUnitAsync("""
            <segment id="s1"><source>a<ph id="1"/></source><target>x</target></segment>
            <segment id="s2"><source>b</source><target>y<ph id="1"/></target></segment>
            """);
        var model = new TargetEditModel(unit, unit.Segments[0]);

        var result = model.CopySource();

        Assert.Equal("a|", Show(model));
        Assert.Contains("'1'", result.Message);
    }

    // Regression (found by the property test): copying the source's annotation halves put a second m1 opening into
    // the unit's target, which the writer refuses.
    [Fact]
    public async Task CopySource_LeavesOutAnnotationHalvesUsedInAnotherSegment()
    {
        var (model, unit) = await EditWithUnitAsync("""
            <segment id="s1"><source><sm id="m1" type="term"/>a</source></segment>
            <segment id="s2"><source>b<em startRef="m1"/></source><target><sm id="m1" type="term"/>y<em startRef="m1"/></target></segment>
            """);

        model.CopySource();

        Assert.Equal("a|", Show(model));
        Assert.Single(unit.Segments.SelectMany(s => s.Target ?? InlineContent.Empty).OfType<InlineMarker>(), m => m.Kind == InlineMarkerKind.Opening);
    }

    [Fact]
    public async Task CopySource_KeepsProtectedCodesTheTargetHad()
    {
        // Code 2 belongs to s2's source but was moved into s1's target; it may not be deleted.
        var unit = await ReadUnitAsync("""
            <segment id="s1"><source>a</source><target>x<ph id="2"/></target></segment>
            <segment id="s2"><source>b<ph id="2" canDelete="no"/></source><target>y</target></segment>
            """);
        var model = new TargetEditModel(unit, unit.Segments[0]);

        model.CopySource();

        Assert.Equal("a⟨2/⟩|", Show(model));
    }

    [Fact]
    public async Task ReplaceTarget_IsOneUndoableEdit()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.InsertText("Nyomd");
        var source = unit.Segments[0].Source.ToList();

        var result = model.ReplaceTarget(new InlineContent([source[1], new InlineText("OK"), source[3], new InlineText(" gomb "), source[5]]));

        Assert.Equal(EditResult.Done, result);
        Assert.Equal("⟨1⟩OK⟨/1⟩ gomb ⟨2/⟩|", Show(model));
        Assert.Equal(model.ToContent(), unit.Segments[0].Target);
        model.Undo();
        Assert.Equal("Nyomd|", Show(model));
    }

    // The same rules as copying the source: code halves already in another segment's target stay out, protected codes stay in.
    [Fact]
    public async Task ReplaceTarget_LeavesOutCodesUsedElsewhere_AndKeepsProtectedCodes()
    {
        var unit = await ReadUnitAsync("""
            <segment id="s1"><source>a<ph id="1"/></source><target>x<ph id="2"/></target></segment>
            <segment id="s2"><source>b<ph id="2" canDelete="no"/><ph id="3"/></source><target>y<ph id="3"/></target></segment>
            """);
        var model = new TargetEditModel(unit, unit.Segments[0]);
        var code3 = unit.Segments[1].Source.OfType<InlineCode>().Last();

        var result = model.ReplaceTarget(new InlineContent([new InlineText("z"), code3]));

        Assert.Equal("z⟨2/⟩|", Show(model));
        Assert.Contains("'3'", result.Message);
        Assert.Contains("Kept 1 code(s)", result.Message);
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public async Task Undo_TypingIsGroupedByWord()
    {
        var model = await EditAsync("""<segment><source>a</source><target></target></segment>""");
        foreach (var c in "Szia világ")
            model.InsertText(c.ToString());

        model.Undo();
        Assert.Equal("Szia |", Show(model));
        model.Undo();
        Assert.Equal("|", Show(model));
        Assert.False(model.CanUndo);
    }

    [Fact]
    public async Task Undo_CodeEditsAreSeparateSteps()
    {
        var model = await EditAsync(Sample);
        model.InsertText("a");
        model.InsertCode("2");
        model.InsertText("b");

        model.Undo();
        Assert.Equal("a⟨2/⟩|", Show(model));
        model.Undo();
        Assert.Equal("a|", Show(model));
    }

    [Fact]
    public async Task Undo_DeletionsAreGrouped_AndTheCaretComesBack()
    {
        var model = await EditAsync("""<segment><source>a</source><target>Hello</target></segment>""");
        model.Delete(DeleteDirection.Backward);
        model.Delete(DeleteDirection.Backward);
        model.Delete(DeleteDirection.Backward);

        model.Undo();

        Assert.Equal("Hello|", Show(model));
    }

    [Fact]
    public async Task Redo_ReappliesUntilANewEdit()
    {
        var model = await EditAsync("""<segment><source>a</source><target></target></segment>""");
        model.InsertText("a");
        model.MoveCaret(1);
        model.InsertText(" b");

        model.Undo();
        Assert.True(model.CanRedo);
        model.Redo();
        Assert.Equal("a b|", Show(model));

        model.Undo();
        model.InsertText("c");
        Assert.False(model.CanRedo);
    }

    [Fact]
    public async Task Undo_WritesTheTargetButNeverChangesTheState()
    {
        var (model, unit) = await EditWithUnitAsync("""<segment><source>a</source><target>x</target></segment>""");
        var segment = unit.Segments[0];
        model.InsertText("y");
        segment.State = SegmentState.Translated; // as the view model does on the first edit

        model.Undo();

        Assert.Equal("x", segment.Target!.ToPlainText());
        Assert.Equal(SegmentState.Translated, segment.State);
    }

    [Fact]
    public async Task EveryAppliedEditRaisesChanged()
    {
        var model = await EditAsync("""<segment><source>a</source><target>x</target></segment>""");
        var changes = 0;
        model.Changed += (_, _) => changes++;

        model.InsertText("y");
        model.Delete(DeleteDirection.Backward);
        model.Undo();
        model.Delete(DeleteDirection.Forward); // at the end: nothing happens

        Assert.Equal(3, changes);
    }

    // ---------------------------------------------------------------- clipboard

    [Fact]
    public async Task SelectedContent_IsTheSelectionWithItsCodes()
    {
        var model = await EditAsync("""<segment><source><pc id="1">a</pc></source><target>x<pc id="1">yz</pc></target></segment>""");
        model.Select(1, 4); // ⟨1⟩yz

        Assert.Equal(new InlineContent([new InlineCode("1", InlineCodeKind.Opening) { CanOverlap = false }, new InlineText("yz")]), model.SelectedContent());
    }

    [Fact]
    public async Task CutAndPaste_MovesACode()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.CopySource(); // Press ⟨1⟩OK⟨/1⟩ for ⟨2/⟩ files.
        model.Select(6, 10);
        var clip = model.SelectedContent();
        model.Delete(DeleteDirection.Backward);
        model.MoveCaret(model.Length - 1);

        var result = model.Paste(clip, unit);

        Assert.Equal(EditResult.Done, result);
        Assert.Equal("Press  for ⟨2/⟩ files⟨1⟩OK⟨/1⟩|.", Show(model));
    }

    [Fact]
    public async Task CopyAndPaste_OfACopyableCode_PastesACopy()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.CopySource();
        model.Select(6, 10);
        var clip = model.SelectedContent();
        model.MoveCaret(model.Length);

        model.Paste(clip, unit);

        var copy = model.ToContent().OfType<InlineCode>().Last();
        Assert.Equal(new InlineCode("3", InlineCodeKind.Closing) { DataRef = "d2", CanOverlap = false }, copy); // a new id, the same original data
        Assert.DoesNotContain(InlineCodeChecker.Check(unit), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task Paste_LeavesOutCodesThatMayNotBeCopied_AndPartsOfCodes()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.CopySource();
        model.Select(9, model.Length); // ⟨/1⟩ for ⟨2/⟩ files.: half of 1, and 2 which may not be copied
        var clip = model.SelectedContent();
        model.MoveCaret(0);

        var result = model.Paste(clip, unit);

        Assert.Equal(" for  files.|Press ⟨1⟩OK⟨/1⟩ for ⟨2/⟩ files.", Show(model));
        Assert.Contains("'1', '2'", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paste_FromAnotherUnit_PastesOnlyTheText()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        var other = await ReadUnitAsync(Sample);
        var clip = new InlineContent([new InlineText("a"), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("b")]);

        var result = model.Paste(clip, other);

        Assert.Equal("ab|", Show(model));
        Assert.Equal("Codes from another unit were left out; only the text was pasted.", result.Message);
        Assert.Equal("ab", unit.Segments[0].Target!.ToPlainText());
    }

    [Fact]
    public async Task Paste_ReplacesTheSelection_KeepingProtectedCodes_AndIsOneUndoStep()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.InsertText("x");
        model.InsertCode("2");
        model.Select(0, model.Length);

        model.Paste(InlineContent.FromText("Hello"), null);
        Assert.Equal("Hello|⟨2/⟩", Show(model));

        model.Undo();
        Assert.Equal("x⟨2/⟩|", Show(model));
        Assert.Equal("x", unit.Segments[0].Target!.ToPlainText());
    }

    [Fact]
    public async Task Paste_AnnotationsComeAlongWhole_AndOnlyOnce()
    {
        var (model, unit) = await EditWithUnitAsync("""<segment><source><mrk id="m1" type="term">file</mrk></source><target><mrk id="m1" type="term">fájl</mrk></target></segment>""");
        model.Select(0, model.Length);
        var whole = model.SelectedContent();
        model.Delete(DeleteDirection.Backward);

        model.Paste(whole, unit);
        Assert.Equal("⟦m1⟧fájl⟦/m1⟧|", Show(model));

        var again = model.Paste(whole, unit); // already in the target: annotations aren't copied
        Assert.Equal("⟦m1⟧fájl⟦/m1⟧fájl|", Show(model));
        Assert.Equal("Left out annotations that are already in the target or were only partly copied.", again.Message);
    }

    [Fact]
    public async Task Paste_NothingUsable_IsRefusedWithTheReason()
    {
        var (model, unit) = await EditWithUnitAsync(Sample);
        model.InsertCode("2");

        var result = model.Paste(new InlineContent([new InlineCode("2", InlineCodeKind.Standalone)]), unit);

        Assert.True(result.IsRefused);
        Assert.Equal("⟨2/⟩|", Show(model));
    }

    [Fact]
    public async Task IsMissing_LooksAtTheWholeUnitsTarget()
    {
        var model = await EditAsync(Sample);
        Assert.True(model.IsMissing("1"));

        model.InsertCode("1");
        Assert.False(model.IsMissing("1"));
        Assert.False(model.IsMissing("unknown"));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<TargetEditModel> EditAsync(string unitContent) => (await EditWithUnitAsync(unitContent)).Model;

    private static async Task<(TargetEditModel Model, TranslationUnit Unit)> EditWithUnitAsync(string unitContent)
    {
        var unit = await ReadUnitAsync(unitContent);
        return (new TargetEditModel(unit, unit.Segments[0]), unit);
    }

    private static async Task<TranslationUnit> ReadUnitAsync(string unitContent)
    {
        var xml = $"""
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1"><unit id="u1">{unitContent}</unit></file>
            </xliff>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return (await new Xliff20Serializer().ReadAsync(stream, TestContext.Current.CancellationToken)).Units.Single();
    }

    // The target as text: ⟨1⟩ ⟨/1⟩ ⟨1/⟩ for codes, ⟦m1⟧ ⟦/m1⟧ for annotations, | for the caret.
    private static string Show(TargetEditModel model)
    {
        var text = new StringBuilder();
        var position = 0;
        foreach (var token in model.Tokens)
        {
            var parts = token switch
            {
                TextToken t => t.Text.Select(c => c.ToString()).ToList(),
                CodeToken { Code.Kind: InlineCodeKind.Standalone } c => [$"⟨{c.Code.Id}/⟩"],
                CodeToken { Code.Kind: InlineCodeKind.Opening } c => [$"⟨{c.Code.Id}⟩"],
                CodeToken c => [$"⟨/{c.Code.Id}⟩"],
                MarkerToken { Marker.Kind: InlineMarkerKind.Opening } m => [$"⟦{m.Marker.Id}⟧"],
                MarkerToken m => [$"⟦/{m.Marker.Id}⟧"],
                _ => throw new InvalidOperationException(),
            };
            foreach (var part in parts)
            {
                if (position++ == model.Caret)
                    text.Append('|');
                text.Append(part);
            }
        }

        if (position == model.Caret)
            text.Append('|');
        return text.ToString();
    }
}
