using System.Xml.Linq;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

/// <summary>Step 2 of <c>docs/design/qa-checks.md</c>: terms from the file's glossary, and the document checks.</summary>
public class QaTerminologyAndDocumentTests
{
    private const string Gls = GlossaryReader.Namespace;

    // ---------------------------------------------------------------- the glossary module

    [Fact]
    public void TheReader_TakesEntriesWithATerm_AndTheirTranslations()
    {
        var unit = UnitWithGlossary("""
            <gls:glossEntry ref="#m1"><gls:term source="tb">TAB key</gls:term><gls:translation>Tab billentyű</gls:translation><gls:translation>tabulátor</gls:translation><gls:definition>A key.</gls:definition></gls:glossEntry>
            <gls:glossEntry><gls:term>file</gls:term></gls:glossEntry>
            <gls:glossEntry ref="#t=m2"><gls:term>folder</gls:term><gls:translation>mappa</gls:translation><gls:translation> mappa </gls:translation></gls:glossEntry>
            <gls:glossEntry><gls:translation>nothing to translate</gls:translation></gls:glossEntry>
            <gls:glossEntry><gls:term>one</gls:term><gls:term>two</gls:term></gls:glossEntry>
            """);

        var entries = GlossaryReader.Read(unit);

        Assert.Equal(
            [("TAB key", "Tab billentyű|tabulátor", "m1"), ("file", "", null), ("folder", "mappa", null)],
            entries.Select(e => (e.Term, string.Join("|", e.Translations), e.MarkerId)));
    }

    // ---------------------------------------------------------------- the terminology check

    [Theory]
    [InlineData("Press the TAB key.", "Nyomd meg a Tab billentyűt.")] // a suffixed translation counts
    [InlineData("Press the TAB key.", "Nyomd meg a tabulátort.")] // any of the translations
    [InlineData("Press the Enter key.", "Nyomd meg az Entert.")] // the term isn't in the source
    public void ATranslatedTerm_IsFine(string source, string target)
    {
        var unit = UnitWithGlossary("""<gls:glossEntry><gls:term>TAB key</gls:term><gls:translation>Tab billentyű</gls:translation><gls:translation>tabulátor</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text(source), Target = Text(target) });

        Assert.Empty(QaChecker.Check(unit));
    }

    [Fact]
    public void ATermTranslatedOtherwise_IsAWarning()
    {
        var unit = UnitWithGlossary("""<gls:glossEntry><gls:term>TAB key</gls:term><gls:translation>Tab billentyű</gls:translation><gls:translation>tabulátor</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text("Press the tab key."), Target = Text("Nyomd meg a tab gombot.") });

        var issue = Assert.Single(QaChecker.Check(unit));
        Assert.Equal((QaIssueKind.Terminology, IssueSeverity.Warning), (issue.Kind, issue.Severity));
        Assert.Equal("The glossary translates “TAB key” as “Tab billentyű” or “tabulátor”, which isn't in the target.", issue.Message);
        Assert.Equal((new TextSpan(0, 10, 7), true), (issue.Span!.Value, issue.SpanInSource));
    }

    // An entry pointing to a term annotation: the annotation's text is the term, and only its segment is checked.
    [Fact]
    public void AnEntryForAnAnnotation_ChecksThatSegment()
    {
        var unit = UnitWithGlossary("""<gls:glossEntry ref="#m1"><gls:term>key</gls:term><gls:translation>billentyű</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text("A key opens it."), Target = Text("Egy kulcs nyitja.") });
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineText("Press the "), new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" }, new InlineText("TAB key"), new InlineMarker("m1", InlineMarkerKind.Closing), new InlineText(".")]),
            Target = Text("Nyomd meg a Tab gombot."),
        });

        var issue = Assert.Single(QaChecker.Check(unit));
        Assert.Same(unit.Segments[1], issue.Segment);
        Assert.Equal("The glossary translates “TAB key” as “billentyű”, which isn't in the target.", issue.Message);
    }

    [Fact]
    public void AnEntryWithoutTranslations_ChecksNothing()
    {
        var unit = UnitWithGlossary("""<gls:glossEntry><gls:term>file</gls:term></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text("Save the file."), Target = Text("Mentsd.") });

        Assert.Empty(QaChecker.Check(unit));
    }

    // Japanese and Chinese have no spaces between words, so a "word" is a whole clause: terms are found anywhere.
    [Theory]
    [InlineData("Save the file.", "このファイルを保存します。", "file", "ファイル", false)]
    [InlineData("Save the file.", "この写真を保存します。", "file", "ファイル", true)]
    [InlineData("このファイルを保存します。", "Save this file.", "ファイル", "file", false)]
    [InlineData("このファイルを保存します。", "Save the photo.", "ファイル", "file", true)]
    public void TermsInScriptsWithoutSpaces_AreFoundAnywhere(string source, string target, string term, string translation, bool reported)
    {
        var unit = UnitWithGlossary($"""<gls:glossEntry><gls:term>{term}</gls:term><gls:translation>{translation}</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text(source), Target = Text(target) });

        Assert.Equal(reported, QaChecker.Check(unit).Any(i => i.Kind == QaIssueKind.Terminology));
    }

    // In mixed text, a Latin word stays a word: "file" isn't in このprofileを開く, but "Windows" is in このWindowsを開く.
    [Theory]
    [InlineData("Open the file.", "このprofileを開く", "file", "file", true)]
    [InlineData("このprofileを開く", "Open the folder.", "file", "folder", false)]
    [InlineData("Open Windows.", "このWindowsを開く", "Windows", "Windows", false)]
    [InlineData("Show the card.", "ガードを表示", "card", "カード", true)] // ガ (ga) isn't カ (ka)
    [InlineData("Show the card.", "カードを表示", "card", "カード", false)]
    public void MixedScripts_KeepWordsApart_AndKanaVoicingCounts(string source, string target, string term, string translation, bool reported)
    {
        var unit = UnitWithGlossary($"""<gls:glossEntry><gls:term>{term}</gls:term><gls:translation>{translation}</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text(source), Target = Text(target) });

        Assert.Equal(reported, QaChecker.Check(unit).Any(i => i.Kind == QaIssueKind.Terminology));
    }

    // The span takes in a combining mark at the term's end, so a highlight never splits a letter from its mark.
    [Fact]
    public void ATermsSpan_KeepsItsLastCombiningMark()
    {
        var unit = UnitWithGlossary("""<gls:glossEntry><gls:term>ไฟล์</gls:term><gls:translation>file</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text("บันทึกไฟล์"), Target = Text("Save the photo.") });

        Assert.Equal(new TextSpan(0, 6, 4), Assert.Single(QaChecker.Check(unit)).Span);
    }

    // An entry for a term annotation and a plain entry for the same words are one term; so are terms spaced differently.
    [Theory]
    [InlineData("Törölje a dokumentumot.", true)]
    [InlineData("Törölje az állományt.", false)]
    [InlineData("Törölje a fájlt.", false)]
    public void EntriesForAnAnnotationAndForItsWords_AreOneTerm(string target, bool reported)
    {
        var unit = UnitWithGlossary("""
            <gls:glossEntry ref="#m1"><gls:term>file</gls:term><gls:translation>fájl</gls:translation></gls:glossEntry>
            <gls:glossEntry><gls:term>file</gls:term><gls:translation>állomány</gls:translation></gls:glossEntry>
            """);
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineText("Delete the "), new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" }, new InlineText("file"), new InlineMarker("m1", InlineMarkerKind.Closing), new InlineText(".")]),
            Target = Text(target),
        });

        var issues = QaChecker.Check(unit);
        Assert.Equal(reported ? 1 : 0, issues.Count);
        if (reported)
            Assert.Equal("The glossary translates “file” as “fájl” or “állomány”, which isn't in the target.", issues[0].Message);
    }

    [Fact]
    public void TermsSpacedDifferently_AreOneTerm()
    {
        var unit = UnitWithGlossary("""
            <gls:glossEntry><gls:term>TAB  key</gls:term><gls:translation>Tab billentyű</gls:translation></gls:glossEntry>
            <gls:glossEntry><gls:term>tab key</gls:term><gls:translation>tabulátor</gls:translation></gls:glossEntry>
            """);
        unit.Segments.Add(new Segment { Source = Text("Press the TAB key."), Target = Text("Nyomd meg a tabulátort.") });

        Assert.Empty(QaChecker.Check(unit));
    }

    // Two entries for one term are one term with both translations, whichever comes first.
    [Theory]
    [InlineData("fájl", "állomány")]
    [InlineData("állomány", "fájl")]
    public void EntriesForTheSameTerm_ShareTheirTranslations(string first, string second)
    {
        var unit = UnitWithGlossary($"""
            <gls:glossEntry><gls:term>file</gls:term><gls:translation>{first}</gls:translation></gls:glossEntry>
            <gls:glossEntry><gls:term>File</gls:term><gls:translation>{second}</gls:translation></gls:glossEntry>
            """);
        unit.Segments.Add(new Segment { Source = Text("Delete the file."), Target = Text("Törölje az állományt.") });
        unit.Segments.Add(new Segment { Source = Text("Delete the file."), Target = Text("Törölje a dokumentumot.") });

        var issue = Assert.Single(QaChecker.Check(unit));
        Assert.Same(unit.Segments[1], issue.Segment);
        Assert.Equal($"The glossary translates “file” as “{first}” or “{second}”, which isn't in the target.", issue.Message);
    }

    [Fact]
    public void ATranslationOfSymbolsOnly_IsFoundAsItIs()
    {
        var unit = UnitWithGlossary("""<gls:glossEntry><gls:term>percent</gls:term><gls:translation>%</gls:translation></gls:glossEntry>""");
        unit.Segments.Add(new Segment { Source = Text("Ten percent."), Target = Text("Tíz %.") });
        unit.Segments.Add(new Segment { Source = Text("Ten percent."), Target = Text("Tíz százalék.") });

        Assert.Equal([unit.Segments[1]], QaChecker.Check(unit).Select(i => i.Segment));
    }

    // ---------------------------------------------------------------- document checks

    [Fact]
    public void UntranslatedSegments_AreReported_UnlessNotToBeTranslated()
    {
        var notToTranslate = Unit("u2", ("Version 2", null));
        notToTranslate.Translate = false;
        var document = Document(Unit("u1", ("Save.", null), ("Open.", "Nyisd meg."), ("Close.", "")), notToTranslate);
        var group = new TranslationGroup { Id = "g1", Translate = false };
        group.Items.Add(Unit("u3", ("Do not translate.", null)));
        document.Files[0].Items.Add(group);

        var issue = Assert.Single(DocumentChecker.Check(document));
        Assert.Equal((QaIssueKind.Untranslated, IssueSeverity.Warning, "Save.", "The segment isn't translated."),
            (issue.Kind, issue.Severity, issue.Segment.Source.ToPlainText(), issue.Message));
    }

    [Fact]
    public void AFileNotToTranslate_HasNothingUntranslated()
    {
        var document = Document(Unit("u1", ("Save.", null)));
        document.Files[0].Translate = false;
        var file = new TranslationFile { Id = "f2" };
        var group = new TranslationGroup { Id = "g1", Translate = true }; // translate="yes" inside a file marked "no"
        group.Items.Add(Unit("u2", ("Open.", null)));
        file.Items.Add(group);
        document.Files.Add(file);
        document.Files[0].Items.Add(Unit("u3", ("Close.", null)));
        document.Files[0].Items.Add(new TranslationGroup { Id = "g2", Translate = true });
        ((TranslationGroup)document.Files[0].Items[^1]).Items.Add(Unit("u4", ("Print.", null)));

        // f1 is marked "no": only its group marked "yes" counts. f2 isn't marked, so its unit counts. Document order.
        Assert.Equal(["u4", "u2"], DocumentChecker.Check(document).Select(i => UnitOf(document, i.Segment)));
        Assert.Equal(["u2", "u4"], document.UnitsToTranslate.Select(u => u.Id).Order());
    }

    // Sources are compared in NFC, as for an exact TM match; a near-repetition is another sentence.
    [Fact]
    public void Consistency_ComparesSourcesInNfc_AndIgnoresNearRepetitions()
    {
        var document = Document(
            Unit("u1", ("Caf\u00E9 menu.", "Kávézó menü.")),
            Unit("u2", ("Cafe\u0301 menu.", "Kávéház menü.")),
            Unit("u3", ("Save the file.", "Mentsd a fájlt.")),
            Unit("u4", ("Save the files.", "Mentsd a fájlokat.")));

        Assert.Equal(["u1", "u2"], DocumentChecker.Check(document).Select(i => UnitOf(document, i.Segment)));
    }

    [Fact]
    public void TheSameSourceTranslatedTwoWays_IsInfoOnEachOccurrence()
    {
        var document = Document(
            Unit("u1", ("Save the file.", "Mentsd a fájlt.")),
            Unit("u2", ("Open.", "Nyisd meg.")),
            Unit("u3", ("Save the file.", "Fájl mentése.")),
            Unit("u4", ("Save the file.", "Mentsd a fájlt.")));

        var issues = DocumentChecker.Check(document);

        Assert.Equal(["u1", "u3", "u4"], issues.Select(i => UnitOf(document, i.Segment)));
        Assert.All(issues, i => Assert.Equal((QaIssueKind.InconsistentTranslation, IssueSeverity.Info, "“Save the file.” is translated 2 ways in this document."), (i.Kind, i.Severity, i.Message)));
        Assert.Equal(["u3"], issues[0].Related.Select(s => UnitOf(document, s)));
        Assert.Equal(["u1", "u4"], issues[1].Related.Select(s => UnitOf(document, s)));
    }

    [Fact]
    public void CodeIdsDontMakeTranslationsDifferent()
    {
        var document = Document();
        foreach (var (id, code) in new[] { ("u1", "1"), ("u2", "7") })
        {
            var unit = new TranslationUnit { Id = id };
            unit.Segments.Add(new Segment
            {
                Source = new InlineContent([new InlineCode(code, InlineCodeKind.Standalone), new InlineText(" files")]),
                Target = new InlineContent([new InlineCode(code, InlineCodeKind.Standalone), new InlineText(" fájl")]),
            });
            document.Files[0].Items.Add(unit);
        }

        Assert.Empty(DocumentChecker.Check(document));
    }

    [Fact]
    public void TheSameTranslationForDifferentSources_IsInfo()
    {
        var document = Document(Unit("u1", ("Save", "Mentés")), Unit("u2", ("Saving", "Mentés")), Unit("u3", ("Clear", "")), Unit("u4", ("Erase", "")));

        Assert.Equal(
            [("u1", "“Mentés” translates 2 different sources in this document."), ("u2", "“Mentés” translates 2 different sources in this document.")],
            DocumentChecker.Check(document).Select(i => (UnitOf(document, i.Segment), i.Message)));
    }

    [Fact]
    public void SwitchedOffDocumentChecks_DontRun()
    {
        var document = Document(Unit("u1", ("Save", null)), Unit("u2", ("Open", "Nyisd")), Unit("u3", ("Open", "Megnyitás")));
        var options = new QaOptions { Disabled = new HashSet<QaIssueKind> { QaIssueKind.Untranslated, QaIssueKind.InconsistentTranslation } };

        Assert.Empty(DocumentChecker.Check(document, options));
    }

    [Fact]
    public void AUnitNotToTranslate_HasNoQualityIssues()
    {
        var unit = Unit("u1", ("Acme Cloud Server", "Acme Cloud Server"), ("Copy 3 files.", "Másolj 4 fájlt"));
        Assert.NotEmpty(QaChecker.Check(unit));

        unit.Translate = false;
        Assert.Empty(QaChecker.Check(unit));
    }

    // ---------------------------------------------------------------- helpers

    private static TranslationUnit UnitWithGlossary(string entries)
    {
        var unit = new TranslationUnit { Id = "u1" };
        unit.ExtensionElements.Add(XElement.Parse($"""<gls:glossary xmlns:gls="{Gls}">{entries}</gls:glossary>"""));
        return unit;
    }

    private static TranslationDocument Document(params TranslationUnit[] units)
    {
        var document = new TranslationDocument { SourceLanguage = "en", TargetLanguage = "hu" };
        var file = new TranslationFile { Id = "f1" };
        file.Items.AddRange(units);
        document.Files.Add(file);
        return document;
    }

    private static TranslationUnit Unit(string id, params (string Source, string? Target)[] segments)
    {
        var unit = new TranslationUnit { Id = id };
        foreach (var (source, target) in segments)
            unit.Segments.Add(new Segment { Source = Text(source), Target = target is null ? null : Text(target) });
        return unit;
    }

    private static string UnitOf(TranslationDocument document, Segment segment) =>
        document.Units.Single(u => u.Segments.Contains(segment)).Id;

    private static InlineContent Text(string text) => text.Length == 0 ? InlineContent.Empty : InlineContent.FromText(text);
}
