using System.Xml.Linq;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

/// <summary>
/// Step 1 of <c>docs/design/termbase.md</c>: the terminology check with term records from outside the file, and the
/// term rule for terms with punctuation.
/// </summary>
public class QaTermRecordTests
{
    private static readonly TermOrigin Client = TermOrigin.OfTermbase("client");

    // ---------------------------------------------------------------- records and origins

    [Fact]
    public void ATermbaseRecordAlone_IsChecked_AndNamesItsTermbase()
    {
        var unit = Unit(("Open the folder.", "Nyisd meg a könyvtárat."));

        var issue = Assert.Single(Check(unit, Record(["folder"], ["mappa"])));
        Assert.Equal((QaIssueKind.Terminology, IssueSeverity.Warning), (issue.Kind, issue.Severity));
        Assert.Equal("The termbase “client” translates “folder” as “mappa”, which isn't in the target.", issue.Message);
        Assert.Equal((new TextSpan(0, 9, 6), true), (issue.Span!.Value, issue.SpanInSource));
    }

    [Theory]
    [InlineData("Nyisd meg a mappát.", false)]
    [InlineData("Nyisd meg a mappa-ablakot.", false)]
    [InlineData("Nyisd meg a könyvtárat.", true)]
    public void ATermbaseRecord_IsSatisfiedByItsTranslation(string target, bool reported)
    {
        var unit = Unit(("Open the folder.", target));

        Assert.Equal(reported, Check(unit, Record(["folder"], ["mappa"])).Count == 1);
    }

    // The same term from the file's glossary and the termbase: one term, both translations, both origins named.
    [Theory]
    [InlineData("Törölje a fájlt.", false)]
    [InlineData("Törölje az állományt.", false)]
    [InlineData("Törölje a dokumentumot.", true)]
    public void TheSameTermFromBothOrigins_IsOneTerm(string target, bool reported)
    {
        var unit = Unit(("Delete the file.", target));
        AddGlossary(unit, """<gls:glossEntry><gls:term>file</gls:term><gls:translation>fájl</gls:translation></gls:glossEntry>""");

        var issues = Check(unit, Record(["File"], ["állomány"]));

        Assert.Equal(reported ? 1 : 0, issues.Count);
        if (reported)
            Assert.Equal("The file's glossary and the termbase “client” translate “file” as “fájl” or “állomány”, which isn't in the target.", issues[0].Message);
    }

    [Fact]
    public void SeveralTermbases_AreAllNamed_TheFilesGlossaryFirst()
    {
        var unit = Unit(("Delete the file.", "Törölje a dokumentumot."));
        AddGlossary(unit, """<gls:glossEntry><gls:term>file</gls:term><gls:translation>fájl</gls:translation></gls:glossEntry>""");

        var issue = Assert.Single(Check(unit,
            Record(["file"], ["állomány"]),
            new TermRecord(["file"], ["akta"], TermOrigin.OfTermbase("own"))));

        Assert.Equal("The file's glossary, the termbase “client” and the termbase “own” translate “file” as “fájl” or “állomány” or “akta”, which isn't in the target.", issue.Message);
    }

    // ---------------------------------------------------------------- synonyms and homonyms

    [Theory]
    [InlineData("Open the directory.", "Nyisd meg a mappát.", null)]
    [InlineData("Open the directory.", "Nyisd meg a könyvtárat.", "directory")]
    [InlineData("Move the folder into the directory.", "Tedd át.", "folder")] // reported once, by the first found
    public void Synonyms_AreOneConcept(string source, string target, string? reported)
    {
        var unit = Unit((source, target));

        var issues = Check(unit, Record(["folder", "directory"], ["mappa"]));

        Assert.Equal(reported is null ? 0 : 1, issues.Count);
        if (reported is not null)
            Assert.Equal($"The termbase “client” translates “{reported}” as “mappa”, which isn't in the target.", issues[0].Message);
    }

    // A synonym shared with another record joins the two: each one's terms are the other's term too.
    [Fact]
    public void RecordsSharingASynonym_AreOneTerm()
    {
        var unit = Unit(("Move the folder into the directory.", "Tedd át a könyvtárba."));

        Assert.Empty(Check(unit, Record(["folder", "directory"], ["mappa"]), Record(["directory"], ["könyvtár"])));
    }

    // Two concepts for "file" (the noun and the verb): checked as one term, so either translation passes.
    [Theory]
    [InlineData("Mentsd a fájlt.", false)]
    [InlineData("Iktatja.", false)]
    [InlineData("Mentsd.", true)]
    public void Homonyms_PassWithEitherTranslation(string target, bool reported)
    {
        var unit = Unit(("Save the file.", target));

        var issues = Check(unit, Record(["file"], ["fájl"]), Record(["file"], ["iktat"]));

        Assert.Equal(reported ? 1 : 0, issues.Count);
        if (reported)
            Assert.Equal("The termbase “client” translates “file” as “fájl” or “iktat”, which isn't in the target.", issues[0].Message);
    }

    // ---------------------------------------------------------------- forbidden translations

    [Theory]
    [InlineData("Nyisd meg a mappát.", false)]
    [InlineData("Nyisd meg a dossziét.", true)]
    public void AForbiddenTranslation_NeverSatisfiesTheCheck(string target, bool reported)
    {
        var unit = Unit(("Open the folder.", target));
        var record = new TermRecord(["folder"], ["mappa", "dosszié"], Client) { ForbiddenTranslations = ["dosszié"] };

        var issues = Check(unit, record);

        Assert.Equal(reported ? 1 : 0, issues.Count);
        if (reported)
            Assert.Equal("The termbase “client” translates “folder” as “mappa”, which isn't in the target.", issues[0].Message);
    }

    // Until the forbidden-term check exists (open question 8), such a record has nothing to check.
    [Fact]
    public void ARecordWithOnlyForbiddenTranslations_ReportsNothing()
    {
        var unit = Unit(("Open the folder.", "Nyisd meg a dossziét."));
        var record = new TermRecord(["folder"], [], Client) { ForbiddenTranslations = ["dosszié"] };

        Assert.Empty(Check(unit, record));
    }

    [Fact]
    public void BlankTermsAndTranslations_AreLeftOut()
    {
        var unit = Unit(("Open the folder.", "Nyisd meg."));

        Assert.Empty(Check(unit, Record(["", "  "], ["mappa"]), Record(["folder"], [" ", ""])));
    }

    // ---------------------------------------------------------------- the term source

    [Fact]
    public void TheTermSource_IsAskedPerSegment_WithItsSource()
    {
        var unit = Unit(("Open the folder.", "Nyisd meg."), ("Not translated.", null), ("Save the file.", "Mentsd."));
        var source = new RecordingSource(Record(["folder"], ["mappa"]));

        var issue = Assert.Single(QaChecker.Check(unit, terms: source));

        Assert.Same(unit.Segments[0], issue.Segment);
        Assert.Equal(["Open the folder.", "Save the file."], source.Asked);
    }

    [Fact]
    public void AUnitNotToTranslate_DoesntAskTheTermSource()
    {
        var unit = Unit(("Open the folder.", "Nyisd meg."));
        unit.Translate = false;
        var source = new RecordingSource(Record(["folder"], ["mappa"]));

        Assert.Empty(QaChecker.Check(unit, terms: source));
        Assert.Empty(source.Asked);
    }

    [Fact]
    public void ASwitchedOffTerminologyCheck_DoesntAskTheTermSource()
    {
        var unit = Unit(("Open the folder.", "Nyisd meg."));
        var source = new RecordingSource(Record(["folder"], ["mappa"]));
        var options = new QaOptions { Disabled = new HashSet<QaIssueKind> { QaIssueKind.Terminology } };

        Assert.Empty(QaChecker.Check(unit, options, source));
        Assert.Empty(source.Asked);
    }

    // ---------------------------------------------------------------- terms with punctuation

    // A term with punctuation is found where the text has the same characters: C# isn't the word "c", .NET isn't
    // "net", and only whitespace may differ.
    [Theory]
    [InlineData("C#", "Learn C# today.", true)]
    [InlineData("C#", "Learn c# today.", true)]
    [InlineData("C#", "Learn C today.", false)]
    [InlineData("C#", "Learn Cobol today.", false)]
    [InlineData("C#", "Learn C#x today.", false)] // ends in punctuation: a word boundary after it
    [InlineData("C#", "Learn C#.", true)]
    [InlineData("C#", "Learn C#", true)]
    [InlineData("C#", "このC#を学ぶ", true)] // a script without spaces next to it is a boundary
    [InlineData("C#", "Learn \U0001D400C# today.", false)] // a letter outside the BMP is a letter
    [InlineData("C#", "Learn C#\U0001D400 today.", false)]
    [InlineData("C++", "Use C++ here.", true)]
    [InlineData("C++", "Use C here.", false)]
    [InlineData("C++", "Use C+ here.", false)]
    [InlineData("C#", "Use C++ here.", false)]
    [InlineData(".NET", "Install .NET 10.", true)]
    [InlineData(".NET", "Install the net.", false)]
    [InlineData(".NET", "Install ASP.NET.", false)] // starts at a word beginning
    [InlineData(".NET", "Install the .NETs.", true)]
    [InlineData("Wi-Fi", "Turn on wi-fi.", true)]
    [InlineData("Wi-Fi", "Turn on Wi Fi.", false)]
    [InlineData("Wi-Fi", "Turn on WiFi.", false)]
    [InlineData("Wi-Fi", "Kapcsold be a Wi-Fi-t.", true)] // a Hungarian suffix
    [InlineData("Wi-Fi", "Turn on HiWi-Fi.", false)]
    [InlineData("e-mail", "Küldj e-mailt.", true)]
    [InlineData("e-mail", "Küldj E-Mailt.", true)]
    [InlineData("e-mail", "Send an email.", false)]
    [InlineData("e-mail", "Send a re-mail.", false)]
    [InlineData("file-based  system", "A file-based system.", true)] // whitespace may differ
    [InlineData("file-based system", "A file-based  system.", true)]
    [InlineData("file-based system", "A file-basedsystem.", false)]
    [InlineData("Straße-Nr.", "Die strasse-nr. fehlt.", true)]
    [InlineData("e-mail", "Küldj é-mailt.", true)] // accents are folded, as for words
    [InlineData("C#", "Learn C#x and C# today.", true)] // a refused place doesn't stop the search
    [InlineData(".NET", "Install ASP.NET or .NET.", true)]
    [InlineData("Wi-Fi", "HiWi-Fi or Wi-Fi.", true)]
    [InlineData("don't", "Don\u2019t panic.", true)] // apostrophes are one character
    [InlineData("l\u2019ordinateur", "Allume l'ordinateur.", true)]
    [InlineData("e-mail", "Küldj e\u2011mailt.", true)] // so are hyphens (a non-breaking one here)
    [InlineData("e-mail", "Küldj e\u2013mailt.", false)] // a dash isn't a hyphen
    [InlineData("x-Gros", "Ein x-großartiger Tag.", true)] // the last word's beginning may end inside ß's "ss"
    public void ATermWithPunctuation_IsFoundOnlyAsWritten(string term, string source, bool found)
    {
        var unit = Unit((source, "–"));

        Assert.Equal(found, Check(unit, Record([term], ["(translation)"])).Count == 1);
    }

    // Translations are found in the target by the same rule.
    [Theory]
    [InlineData("C#", "Tanuld a C#-ot.", true)]
    [InlineData("C#", "Tanuld a C-t.", false)]
    [InlineData("C#", "Tanuld a C#x-et.", false)]
    [InlineData("e-mail", "Küldj e-mailt.", true)]
    [InlineData("e-mail", "Küldj emailt.", false)]
    [InlineData("Wi-Fi", "Kapcsold be a Wi-Fi-t.", true)]
    [InlineData(".NET", "Telepítsd az ASP.NET-et.", false)]
    public void ATranslationWithPunctuation_IsFoundOnlyAsWritten(string translation, string target, bool found)
    {
        var unit = Unit(("Use the language.", target));

        Assert.Equal(found, Check(unit, Record(["language"], [translation])).Count == 0);
    }

    // Translations are found whichever apostrophe or hyphen the target uses.
    [Theory]
    [InlineData("l'ordinateur", "Allume l\u2019ordinateur.", true)]
    [InlineData("e-mail", "Küldj e\u2011mailt.", true)]
    public void ATranslationWithATypographicVariant_IsFound(string translation, string target, bool found)
    {
        var unit = Unit(("Use the language.", target));

        Assert.Equal(found, Check(unit, Record(["language"], [translation])).Count == 0);
    }

    // Codes are left out of a term with punctuation, as the word rule leaves them out: one at a gap is whitespace,
    // one inside the term is skipped. In the source the term is found (and reported), in the target it satisfies.
    [Theory]
    [MemberData(nameof(CodedTexts))]
    public void ATermWithPunctuation_IsFoundAcrossCodes(InlineContent coded)
    {
        var inSource = new TranslationUnit { Id = "u1" };
        inSource.Segments.Add(new Segment { Source = coded, Target = InlineContent.FromText("–") });
        var inTarget = Unit(("Open the Wi-Fi settings.", "–"));
        inTarget.Segments[0].Target = coded;

        Assert.Single(Check(inSource, Record(["Wi-Fi settings"], ["(translation)"])));
        Assert.Empty(Check(inTarget, Record(["Wi-Fi settings"], ["Wi-Fi settings"])));
    }

    public static TheoryData<InlineContent> CodedTexts() =>
    [
        new InlineContent([new InlineText("the "), new InlineCode("1", InlineCodeKind.Opening), new InlineText("Wi-Fi"), new InlineCode("1", InlineCodeKind.Closing), new InlineText(" settings")]),
        new InlineContent([new InlineText("the Wi-Fi"), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("settings")]),
        new InlineContent([new InlineText("the Wi"), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("-Fi settings")]),
    ];

    // A refused place before it doesn't move the span.
    [Fact]
    public void ATermWithPunctuation_SpansTheFirstPlaceItMayBe()
    {
        var unit = Unit(("Learn C#x and C# today.", "–"));

        Assert.Equal(new TextSpan(0, 14, 2), Assert.Single(Check(unit, Record(["C#"], ["x"]))).Span);
    }

    // The span covers the term as written in the text, whitespace and suffix-free.
    [Fact]
    public void ATermWithPunctuation_SpansWhatItMatched()
    {
        var unit = Unit(("Turn on the WI-FI-t now.", "–"));

        Assert.Equal(new TextSpan(0, 12, 5), Assert.Single(Check(unit, Record(["Wi-Fi"], ["x"]))).Span);
    }

    // The file's glossary uses the same rule.
    [Theory]
    [InlineData("Learn C today.", false)]
    [InlineData("Learn C# today.", true)]
    public void TheFilesGlossary_UsesThePunctuationRuleToo(string source, bool reported)
    {
        var unit = Unit((source, "Tanulj."));
        AddGlossary(unit, """<gls:glossEntry><gls:term>C#</gls:term><gls:translation>C#</gls:translation></gls:glossEntry>""");

        Assert.Equal(reported, QaChecker.Check(unit).Any(i => i.Kind == QaIssueKind.Terminology));
    }

    // ---------------------------------------------------------------- helpers

    private static TermRecord Record(IReadOnlyList<string> terms, IReadOnlyList<string> translations) => new(terms, translations, Client);

    private static IReadOnlyList<QaIssue> Check(TranslationUnit unit, params TermRecord[] records) =>
        [.. QaChecker.Check(unit, terms: new RecordingSource(records)).Where(i => i.Kind == QaIssueKind.Terminology)];

    private static TranslationUnit Unit(params (string Source, string? Target)[] segments)
    {
        var unit = new TranslationUnit { Id = "u1" };
        foreach (var (source, target) in segments)
            unit.Segments.Add(new Segment { Source = InlineContent.FromText(source), Target = target is null ? null : InlineContent.FromText(target) });
        return unit;
    }

    private static void AddGlossary(TranslationUnit unit, string entries) =>
        unit.ExtensionElements.Add(XElement.Parse($"""<gls:glossary xmlns:gls="{GlossaryReader.Namespace}">{entries}</gls:glossary>"""));

    /// <summary>Returns the same records for every segment, and records the sources it was asked about.</summary>
    private sealed class RecordingSource(params TermRecord[] records) : ITermSource
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyList<TermRecord> Find(QaText source)
        {
            Asked.Add(source.Text);
            return records;
        }
    }
}
