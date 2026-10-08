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
