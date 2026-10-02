using System.Xml.Linq;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;

namespace SchrodingersCat.Core.Tests;

/// <summary>Groups, modules/extensions and the structural attributes around units.</summary>
public class Xliff20StructureTests : Xliff20SerializerTestBase
{
    private const string MtcNamespace = "urn:oasis:names:tc:xliff:matches:2.0";
    private const string GlsNamespace = "urn:oasis:names:tc:xliff:glossary:2.0";
    private const string MdaNamespace = "urn:oasis:names:tc:xliff:metadata:2.0";
    private const string MyNamespace = "urn:example:tool";

    private const string NestedGroups = """
        <unit id="u0"><segment><source>Zero</source></segment></unit>
        <group id="g1" name="dialog" type="my:dialog" translate="no" canResegment="no" srcDir="ltr" trgDir="rtl">
          <notes><note>About dialog</note></notes>
          <unit id="u1"><segment><source>One</source></segment></unit>
          <group id="g2">
            <unit id="u2"><segment><source>Two</source></segment></unit>
          </group>
          <group id="g3"/>
        </group>
        <unit id="u3"><segment><source>Three</source></segment></unit>
        """;

    private const string Modules = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:mtc="urn:oasis:names:tc:xliff:matches:2.0"
               xmlns:gls="urn:oasis:names:tc:xliff:glossary:2.0" xmlns:mda="urn:oasis:names:tc:xliff:metadata:2.0"
               xmlns:fs="urn:oasis:names:tc:xliff:fs:2.0" xmlns:my="urn:example:tool"
               version="2.0" srcLang="en" trgLang="hu" my:job="42">
          <file id="f1" fs:fs="html" my:origin="cms">
            <skeleton><my:template>&lt;html&gt;  <my:slot ref="u1"/></my:template></skeleton>
            <mda:metadata><mda:metaGroup category="project"><mda:meta type="client">ACME</mda:meta></mda:metaGroup></mda:metadata>
            <my:settings xmlns="urn:example:tool" mode="strict"><rule/></my:settings>
            <notes><note id="n1" my:author="dev">File note</note></notes>
            <group id="g1" fs:fs="div">
              <mda:metadata><mda:metaGroup><mda:meta type="screen">About</mda:meta></mda:metaGroup></mda:metadata>
              <unit id="u1" fs:fs="p" my:hash="abc">
                <mtc:matches>
                  <mtc:match ref="#s1" similarity="95.5"><source><pc id="1">Bold</pc> text</source><target><pc id="1">Félkövér</pc>  szöveg</target></mtc:match>
                </mtc:matches>
                <gls:glossary><gls:glossEntry ref="#m1"><gls:term source="acme.com">ACME</gls:term></gls:glossEntry></gls:glossary>
                <segment id="s1"><source><pc id="1">Bold</pc> text by <mrk id="m1" type="term">ACME</mrk></source></segment>
              </unit>
            </group>
          </file>
        </xliff>
        """;

    [Fact]
    public async Task Read_KeepsGroupNesting()
    {
        var file = (await ReadAsync(WrapUnits(NestedGroups))).Files.Single();

        Assert.Equal(["u0", "u1", "u2", "u3"], file.Units.Select(u => u.Id));
        Assert.Collection(file.Items,
            item => Assert.Equal("u0", Assert.IsType<TranslationUnit>(item).Id),
            item =>
            {
                var g1 = Assert.IsType<TranslationGroup>(item);
                Assert.Equal(("g1", "dialog", "my:dialog"), (g1.Id, g1.Name, g1.Type));
                Assert.Equal((false, false, "ltr", "rtl"), (g1.Translate, g1.CanResegment, g1.SourceDir, g1.TargetDir));
                Assert.Equal("About dialog", Assert.Single(g1.Notes).Text);
                Assert.Equal(["u1", "g2", "g3"], g1.Items.Select(i => i.Id));
                Assert.Equal(["u1", "u2"], g1.Units.Select(u => u.Id));
                Assert.Empty(Assert.IsType<TranslationGroup>(g1.Items[2]).Items);
            },
            item => Assert.Equal("u3", Assert.IsType<TranslationUnit>(item).Id));
    }

    [Fact]
    public async Task RoundTrip_KeepsGroupNesting()
    {
        var xml = await AssertStableRoundTripAsync(WrapUnits(NestedGroups));

        Assert.Contains("""<group id="g1" name="dialog" canResegment="no" translate="no" srcDir="ltr" trgDir="rtl" type="my:dialog">""", xml);
        Assert.Contains("<group id=\"g3\" />", xml);
        var g2 = XDocument.Parse(xml).Descendants(Ns("group")).Single(g => (string?)g.Attribute("id") == "g2");
        Assert.Equal("g1", (string?)g2.Parent!.Attribute("id"));
        Assert.Equal(["u2"], g2.Elements(Ns("unit")).Select(u => (string?)u.Attribute("id")));
    }

    [Fact]
    public async Task Read_KeepsModulesAndExtensions()
    {
        var document = await ReadAsync(Modules);
        var file = document.Files.Single();
        var group = Assert.IsType<TranslationGroup>(Assert.Single(file.Items));
        var unit = Assert.IsType<TranslationUnit>(Assert.Single(group.Items));

        Assert.Equal([new ExtensionAttr(MyNamespace, "job", "42")], document.ExtensionAttributes);
        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "html"), new ExtensionAttr(MyNamespace, "origin", "cms")], file.ExtensionAttributes);
        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "div")], group.ExtensionAttributes);
        Assert.Equal([new ExtensionAttr(FsNamespace, "fs", "p"), new ExtensionAttr(MyNamespace, "hash", "abc")], unit.ExtensionAttributes);
        Assert.Equal([new ExtensionAttr(MyNamespace, "author", "dev")], Assert.Single(file.Notes).ExtensionAttributes);

        Assert.Equal(XName.Get("skeleton", "urn:oasis:names:tc:xliff:document:2.0"), file.Skeleton!.Name);
        Assert.Equal([XName.Get("metadata", MdaNamespace), XName.Get("settings", MyNamespace)], file.ExtensionElements.Select(e => e.Name));
        Assert.Equal([XName.Get("metadata", MdaNamespace)], group.ExtensionElements.Select(e => e.Name));
        Assert.Equal([XName.Get("matches", MtcNamespace), XName.Get("glossary", GlsNamespace)], unit.ExtensionElements.Select(e => e.Name));

        // Kept detached from the source document, so editing the model can't reach back into it.
        Assert.All(file.ExtensionElements, e => Assert.Null(e.Parent));
    }

    [Fact]
    public async Task RoundTrip_KeepsModulesAndExtensions()
    {
        var original = await ReadAsync(Modules);
        var xml = await AssertStableRoundTripAsync(Modules);
        var reread = await ReadAsync(xml);

        AssertSameElements(original.Files[0].ExtensionElements, reread.Files[0].ExtensionElements);
        AssertSameElements([original.Files[0].Skeleton!], [reread.Files[0].Skeleton!]);
        AssertSameElements(original.Units.Single().ExtensionElements, reread.Units.Single().ExtensionElements);
        Assert.Equal(original.ExtensionAttributes, reread.ExtensionAttributes);
        Assert.Equal(original.Units.Single().ExtensionAttributes, reread.Units.Single().ExtensionAttributes);

        // Whitespace inside module content is kept exactly: XmlWriter must not indent into it.
        Assert.Contains("""<target><pc id="1">Félkövér</pc>  szöveg</target>""", xml);
        Assert.Contains("""<skeleton><my:template>&lt;html&gt;  <my:slot ref="u1" /></my:template></skeleton>""", xml);
        Assert.Contains("""xmlns:mtc="urn:oasis:names:tc:xliff:matches:2.0""", xml);
    }

    [Fact]
    public async Task Write_ExtensionElementsAddedToTheModel()
    {
        var document = DocumentWith(new InlineText("x"));
        var unit = document.Units.Single();
        unit.ExtensionElements.Add(new XElement(XName.Get("note", MyNamespace), new XAttribute("by", "me"), "Checked"));
        unit.ExtensionAttributes.Add(new ExtensionAttr(MyNamespace, "flag", "1"));

        var xml = await WriteAsync(document);

        Assert.Contains("""<unit id="u1" ns1:flag="1">""", xml);
        Assert.Contains("""<ns1:note by="me">Checked</ns1:note>""", xml);
        Assert.Null(unit.ExtensionElements[0].Parent); // the model's instance is copied, not attached
    }

    [Fact]
    public async Task RoundTrip_KeepsStructuralAttributes()
    {
        const string xml = """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu" xml:space="default">
              <file id="f1" canResegment="no" translate="yes" srcDir="auto" trgDir="rtl" xml:space="preserve">
                <unit id="u1" type="my:label" srcDir="rtl">
                  <segment id="s1" canResegment="yes"><source xml:lang="EN">One.</source><target xml:lang="hu" order="2">Egy.</target></segment>
                  <ignorable><source> </source></ignorable>
                  <segment id="s2"><source>Two.</source><target order="1">Kettő.</target></segment>
                </unit>
              </file>
            </xliff>
            """;

        var written = await AssertStableRoundTripAsync(xml);
        var file = (await ReadAsync(written)).Files.Single();
        var unit = file.Units.Single();

        Assert.Equal((false, true, "auto", "rtl"), (file.CanResegment, file.Translate, file.SourceDir, file.TargetDir));
        Assert.Equal(("my:label", "rtl", null), (unit.Type, unit.SourceDir, unit.TargetDir));
        Assert.Equal([true, null, null], unit.Segments.Select(s => s.CanResegment));
        Assert.Equal([2, null, 1], unit.Segments.Select(s => s.TargetOrder));
        Assert.DoesNotContain("xml:lang", written); // it can only repeat srcLang/trgLang
    }

    [Theory]
    [InlineData("""<unit id="u1"><segment><source>a</source></segment><segmnt/></unit>""")]
    [InlineData("""<unit id="u1" xmlns:x="urn:x"><segment><source>a</source></segment><foo xmlns=""/></unit>""")]
    [InlineData("""<unit id="u1" xmlns:x="urn:x"><segment><x:ext/><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1" xmlns:x="urn:x"><segment x:a="1"><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1" xmlns:x="urn:x"><segment><source x:a="1">a</source></segment></unit>""")]
    [InlineData("""<unit id="u1" foo="1"><segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1">text<segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1"><notes><note>a</note></notes><notes><note>b</note></notes><segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1"><notes/><segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1"><notes><note>a<b/></note></notes><segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1"><originalData><data id="d1">a</data></originalData><originalData><data id="d2">b</data></originalData><segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1"><segment><target>b</target><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1"><segment><source>a</source><target>b</target><target>c</target></segment></unit>""")]
    [InlineData("""<unit id="u1"><segment><source>a</source><target order="0">b</target></segment></unit>""")]
    [InlineData("""<unit id="u1"><segment><source>a</source><target xml:lang="de">b</target></segment></unit>""")]
    [InlineData("""<unit id="u1"><ignorable state="final"><source> </source></ignorable><segment><source>a</source></segment></unit>""")]
    [InlineData("""<unit id="u1" srcDir="up"><segment><source>a</source></segment></unit>""")]
    [InlineData("""<group id="g"/><group id="g"/>""")]
    [InlineData("""<group id="g"><unit id="u1"><segment><source>a</source></segment></unit></group><unit id="u1"><segment><source>b</source></segment></unit>""")]
    [InlineData("""<group/>""")]
    [InlineData("""<skeleton/>""")]
    [InlineData("""<skeleton><unit id="u1"/></skeleton>""")]
    [InlineData("""<skeleton href="a.skl"/><skeleton href="b.skl"/>""")]
    public async Task Read_RejectsStructureItCannotKeep(string fileContent)
    {
        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(WrapUnits(fileContent)));
    }

    [Fact]
    public async Task Read_RejectsDuplicateFileIds()
    {
        const string xml = """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en">
              <file id="f1"><unit id="u1"><segment><source>a</source></segment></unit></file>
              <file id="f1"><unit id="u1"><segment><source>b</source></segment></unit></file>
            </xliff>
            """;

        await Assert.ThrowsAsync<XliffFormatException>(() => ReadAsync(xml));
    }

    public static TheoryData<string, Action<TranslationDocument>> InvalidModels => new()
    {
        { "duplicate unit id across groups", d => d.Files[0].Items.Add(Group("g1", Unit("u1"))) },
        { "duplicate group id", d => d.Files[0].Items.AddRange([Group("g1"), Group("g1", Group("g2"))]) },
        { "duplicate file id", d => d.Files.Add(new TranslationFile { Id = "f1" }) },
        { "invalid direction", d => d.Files[0].SourceDir = "up" },
        { "target order not positive", d => d.Units.Single().Segments[0] = new Segment { Source = Text("a"), Target = Text("b"), TargetOrder = 0 } },
    };

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public async Task Write_RejectsModelsTheReaderWouldReject(string name, Action<TranslationDocument> change)
    {
        _ = name; // only names the test case
        var document = DocumentWith(new InlineText("x"));
        change(document);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(document));
    }

    public static TheoryData<string, Action<TranslationDocument>> UnrepresentableModels => new()
    {
        { "extension element in the XLIFF namespace", d => d.Units.Single().ExtensionElements.Add(new XElement(Ns("notes"))) },
        { "extension element without a namespace", d => d.Files[0].ExtensionElements.Add(new XElement("settings")) },
        { "skeleton that isn't <skeleton>", d => d.Files[0].Skeleton = new XElement(XName.Get("template", MyNamespace)) },
        { "extension attribute in the XLIFF namespace", d => d.Files[0].ExtensionAttributes.Add(new ExtensionAttr(Ns("x").NamespaceName, "x", "1")) },
        { "ignorable with a state", d => d.Units.Single().Segments.Add(new Segment { Source = Text(" "), IsIgnorable = true, State = SegmentState.Final }) },
        { "target order without a target", d => d.Units.Single().Segments[0].TargetOrder = 1 },
    };

    [Theory]
    [MemberData(nameof(UnrepresentableModels))]
    public async Task Write_RejectsModelsXliffCannotRepresent(string name, Action<TranslationDocument> change)
    {
        _ = name; // only names the test case
        var document = DocumentWith(new InlineText("x"));
        change(document);

        await Assert.ThrowsAsync<NotSupportedException>(() => WriteAsync(document));
    }

    /// <summary>Reads, writes, reads and writes again; asserts the two writes match and returns the XML.</summary>
    private async Task<string> AssertStableRoundTripAsync(string xml)
    {
        var written = await WriteAsync(await ReadAsync(xml));
        Assert.Equal(written, await WriteAsync(await ReadAsync(written)));
        return written;
    }

    private static void AssertSameElements(IEnumerable<XElement> expected, IEnumerable<XElement> actual)
    {
        Assert.Equal(expected.Count(), actual.Count());
        foreach (var (e, a) in expected.Zip(actual))
            Assert.True(XNode.DeepEquals(WithoutNamespaceDeclarations(e), WithoutNamespaceDeclarations(a)), $"Expected:\n{e}\nActual:\n{a}");
    }

    // Prefixes are not data: compare names and content only.
    private static XElement WithoutNamespaceDeclarations(XElement element)
    {
        var copy = new XElement(element);
        copy.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        return copy;
    }

    private static XName Ns(string localName) => XName.Get(localName, "urn:oasis:names:tc:xliff:document:2.0");

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private static TranslationUnit Unit(string id)
    {
        var unit = new TranslationUnit { Id = id };
        unit.Segments.Add(new Segment { Source = Text("a") });
        return unit;
    }

    private static TranslationGroup Group(string id, params TranslationItem[] items)
    {
        var group = new TranslationGroup { Id = id };
        group.Items.AddRange(items);
        return group;
    }
}
