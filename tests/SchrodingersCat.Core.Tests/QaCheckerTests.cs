using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

/// <summary>The segment checks of <c>docs/design/qa-checks.md</c>: what each reports, and what it must not.</summary>
public class QaCheckerTests
{
    // ---------------------------------------------------------------- numbers

    [Theory]
    [InlineData("Copy 3 files.", "Másolj 3 fájlt.")]
    [InlineData("It costs 1,000.50 euros.", "1 000,50 euróba kerül.")]
    [InlineData("It costs 1,000.50 euros.", "1.000,5 euróba kerül.")]
    [InlineData("Version 1.2.3", "1.2.3-as verzió")]
    [InlineData("Press F5 or Ctrl+1.", "Nyomd meg az F5-öt vagy a Ctrl+1-et.")]
    [InlineData("Files: 2 and 10.", "Fájlok: 10 és 2.")]
    [InlineData("١٢ items", "12 elem")] // Arabic-Indic digits
    public void Numbers_EqualInValue_AreFine(string source, string target) =>
        Assert.DoesNotContain(Check(source, target), i => i.Kind == QaIssueKind.Numbers);

    [Theory]
    [InlineData("It's 12.5% faster.", "125%-kal gyorsabb.", "The number 12.5 isn't in the target.", "The target has the number 125, which the source doesn't.")]
    [InlineData("Copy 3 files.", "Másold a fájlokat.", "The number 3 isn't in the target.")]
    [InlineData("Copy the files.", "Másolj 2 fájlt.", "The target has the number 2, which the source doesn't.")]
    [InlineData("Wait 2 or 2 seconds.", "Várj 2 másodpercet.", "The number 2 isn't in the target.")]
    public void Numbers_Differing_AreWarnings(string source, string target, params string[] messages)
    {
        var issues = Check(source, target).Where(i => i.Kind == QaIssueKind.Numbers).ToList();
        Assert.Equal(messages, issues.Select(i => i.Message));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
    }

    [Fact]
    public void Numbers_MarkTheirPlace()
    {
        var issues = Check("Copy 3 files.", "Másolj 4 fájlt.").Where(i => i.Kind == QaIssueKind.Numbers).ToList();

        Assert.Equal([(new TextSpan(0, 5, 1), true), (new TextSpan(0, 7, 1), false)], issues.Select(i => (i.Span!.Value, i.SpanInSource)));
    }

    [Fact]
    public void Numbers_InsideDoNotTranslate_AreLeftToThatCheck()
    {
        var source = new InlineContent([new InlineText("Open "), Marker("m1", translate: false), new InlineText("Office 365"), Close("m1"), new InlineText(".")]);

        Assert.DoesNotContain(Check(source, Text("Nyisd meg az Office 365-öt.")), i => i.Kind == QaIssueKind.Numbers);

        // The protected 365 isn't reported as missing; the target's 360 is a number the source doesn't have.
        Assert.Equal(
            [(QaIssueKind.Numbers, "The target has the number 360, which the source doesn't."), (QaIssueKind.DoNotTranslate, "“Office 365” is marked not to be translated, but the target doesn't have it unchanged.")],
            Check(source, Text("Nyisd meg az Office 360-at.")).Select(i => (i.Kind, i.Message)));
    }

    // ---------------------------------------------------------------- same as source

    [Fact]
    public void ATargetCopiedFromTheSource_IsAWarning()
    {
        var issue = Assert.Single(Check("Save the file.", "Save the file."));
        Assert.Equal((QaIssueKind.SameAsSource, IssueSeverity.Warning), (issue.Kind, issue.Severity));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("{0}")]
    [InlineData("42")]
    public void AShortSourceCopied_IsFine(string source) => Assert.Empty(Check(source, source));

    // ---------------------------------------------------------------- translate="no"

    [Fact]
    public void DoNotTranslateText_MustBeInTheTargetUnchanged()
    {
        var source = new InlineContent([new InlineText("Open "), Marker("m1", translate: false), new InlineText("Contoso Mail"), Close("m1"), new InlineText(".")]);

        Assert.Empty(Check(source, new InlineContent([new InlineText("Nyisd meg a "), Marker("m1", translate: false), new InlineText("Contoso Mail"), Close("m1"), new InlineText(" programot.")])));
        Assert.Empty(Check(source, Text("Nyisd meg a Contoso Mail programot."))); // the annotation itself isn't needed

        var issue = Assert.Single(Check(source, Text("Nyisd meg a Contoso Levelezőt.")));
        Assert.Equal((QaIssueKind.DoNotTranslate, "“Contoso Mail” is marked not to be translated, but the target doesn't have it unchanged."), (issue.Kind, issue.Message));
        Assert.Equal((new TextSpan(2, 0, 12), true), (issue.Span!.Value, issue.SpanInSource));
    }

    [Fact]
    public void DoNotTranslate_OpenedInAnEarlierSegment_CoversTheNextOne()
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = new InlineContent([new InlineText("Run "), Marker("m1", translate: false), new InlineText("setup")]) });
        unit.Segments.Add(new Segment { Source = new InlineContent([new InlineText(".exe"), Close("m1"), new InlineText(" now.")]), Target = Text("Futtasd most.") });

        var issue = Assert.Single(QaChecker.Check(unit));
        Assert.Equal("“.exe” is marked not to be translated, but the target doesn't have it unchanged.", issue.Message);
    }

    // A source that is all protected text is meant to be copied: no "same as source" for it.
    [Fact]
    public void ASourceAllMarkedNotToTranslate_MayBeCopied() =>
        Assert.Empty(Check(new InlineContent([Marker("m1", translate: false), new InlineText("Acme Widget Pro"), Close("m1")]), Text("Acme Widget Pro")));

    [Fact]
    public void TranslatableAnnotations_AreNotProtected() =>
        Assert.Empty(Check(new InlineContent([Marker("m1", translate: null), new InlineText("Save"), Close("m1")]), Text("Mentés")));

    // ---------------------------------------------------------------- repeated words

    [Fact]
    public void ARepeatedWord_IsAWarning()
    {
        var issue = Assert.Single(Check("Save the file.", "Mentsd a A fájlt."));
        Assert.Equal((QaIssueKind.RepeatedWord, "“a A”: a word is repeated."), (issue.Kind, issue.Message));
        Assert.Equal(new TextSpan(0, 7, 3), issue.Span);
    }

    [Theory]
    [InlineData("Visit Bora Bora.", "Látogass el Bora Borára... Bora Bora!")] // the source repeats it too
    [InlineData("Save it.", "Mentsd, mentsd.")] // punctuation between
    [InlineData("Wait 2 2 seconds.", "Várj 2 2 másodpercet.")] // numbers aren't words
    public void NotRepeatedWords(string source, string target) =>
        Assert.DoesNotContain(Check(source, target), i => i.Kind == QaIssueKind.RepeatedWord);

    // ---------------------------------------------------------------- spaces

    [Theory]
    [InlineData("Save the file.", "Mentsd a  fájlt.", "The target has a double space.")]
    [InlineData("Save the file.", " Mentsd a fájlt.", "The target starts with a space, the source with no whitespace.")]
    [InlineData("Save the file. ", "Mentsd a fájlt.", "The target ends with no whitespace, the source with a space.")]
    public void Spaces_AreInfo(string source, string target, string message)
    {
        var issue = Assert.Single(Check(source, target));
        Assert.Equal((QaIssueKind.Spaces, IssueSeverity.Info, message), (issue.Kind, issue.Severity, issue.Message));
    }

    [Fact]
    public void ADoubleSpaceInTheSourceToo_IsFine() => Assert.Empty(Check("Name:  value.", "Név:  érték."));

    // ---------------------------------------------------------------- end punctuation

    [Theory]
    [InlineData("Save the file.", "Mentsd a fájlt")]
    [InlineData("Save the file?", "Mentsd a fájlt.")]
    [InlineData("Saved!", "Mentve:")]
    public void DifferentEndPunctuation_IsInfo(string source, string target)
    {
        var issue = Assert.Single(Check(source, target));
        Assert.Equal((QaIssueKind.EndPunctuation, IssueSeverity.Info), (issue.Kind, issue.Severity));
    }

    [Theory]
    [InlineData("Save the file.", "ファイルを保存します。")]
    [InlineData("Do you want to save?", "Θέλετε να αποθηκεύσετε;")]
    [InlineData("He said \"Go.\"", "Azt mondta: „Menj.”")]
    [InlineData("Loading...", "Betöltés…")]
    [InlineData("Save the file", "Mentsd a fájlt.")] // only what the source ends with counts
    public void EquivalentEndPunctuation_IsFine(string source, string target) =>
        Assert.DoesNotContain(Check(source, target), i => i.Kind == QaIssueKind.EndPunctuation);

    [Fact]
    public void EndPunctuation_SaysWhatTheTargetEndsWith() =>
        Assert.Equal(
            ["The source ends with “.”, the target with no punctuation.", "The source ends with “?”, the target with “!”."],
            new[] { Check("Save.", "Mentés"), Check("Save?", "Mentés!") }.Select(i => Assert.Single(i).Message));

    // ---------------------------------------------------------------- brackets and quotes

    [Theory]
    [InlineData("Save (all) files.", "Ments (minden fájlt.", "(")]
    [InlineData("Save [all] files.", "Ments minden] fájlt.", "]")]
    [InlineData("Say \"hi\".", "Mondd: „szia.", "„")]
    public void AnUnpairedBracketOrQuote_IsInfo(string source, string target, string mark)
    {
        var issue = Assert.Single(Check(source, target));
        Assert.Equal((QaIssueKind.Brackets, $"The target has a “{mark}” without its other half."), (issue.Kind, issue.Message));
    }

    [Theory]
    [InlineData("Say \"hi\".", "Mondd: „szia”.")]
    [InlineData("Say \"hi\".", "Sag „hallo“.")]
    [InlineData("Say \"hi\".", "Dis « salut ».")]
    [InlineData("Smile :)", "Mosolyogj :)")] // the source isn't paired either
    public void PairedOrAsInTheSource_IsFine(string source, string target) =>
        Assert.DoesNotContain(Check(source, target), i => i.Kind == QaIssueKind.Brackets);

    // ---------------------------------------------------------------- codes, scope and options

    [Fact]
    public void Codes_StandForTheirEquivalentOrSeparateWords()
    {
        var source = new InlineContent([new InlineText("Line"), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("line.")]);

        // "Line<br/>line" isn't a repeated word, and an equiv counts as text: {0} files.
        Assert.Empty(Check(source, new InlineContent([new InlineText("Sor"), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("sor.")])));
        var count = new InlineContent([new InlineCode("1", InlineCodeKind.Standalone) { Equiv = "3" }, new InlineText(" files.")]);
        Assert.Empty(Check(count, Text("3 fájl.")));
    }

    [Fact]
    public void UntranslatedAndIgnorableSegments_AreNotChecked()
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = Text("Copy 3 files.") });
        unit.Segments.Add(new Segment { Source = Text("  "), Target = Text(" "), IsIgnorable = true });

        Assert.Empty(QaChecker.Check(unit));
    }

    [Fact]
    public void SwitchedOffChecks_DontRun()
    {
        var options = new QaOptions { Disabled = new HashSet<QaIssueKind> { QaIssueKind.Numbers } };

        Assert.Equal([QaIssueKind.EndPunctuation], QaChecker.Check(Unit(Text("Copy 3 files."), Text("Másolj 4 fájlt")), options).Select(i => i.Kind));
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<QaIssue> Check(string source, string target) => Check(Text(source), Text(target));

    private static IReadOnlyList<QaIssue> Check(InlineContent source, InlineContent target) => QaChecker.Check(Unit(source, target));

    private static TranslationUnit Unit(InlineContent source, InlineContent target)
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment { Source = source, Target = target });
        return unit;
    }

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private static InlineMarker Marker(string id, bool? translate) => new(id, InlineMarkerKind.Opening) { Translate = translate };

    private static InlineMarker Close(string id) => new(id, InlineMarkerKind.Closing);
}
