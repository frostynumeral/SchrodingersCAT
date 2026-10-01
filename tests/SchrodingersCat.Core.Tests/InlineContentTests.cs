using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests;

public class InlineContentTests
{
    private static readonly InlineCode BoldStart = new("1", InlineCodeKind.Opening) { Type = "fmt", SubType = "xlf:b" };
    private static readonly InlineCode BoldEnd = new("1", InlineCodeKind.Closing) { Type = "fmt", SubType = "xlf:b" };

    [Fact]
    public void FromText_EmptyString_IsEmpty()
    {
        Assert.True(InlineContent.FromText("").IsEmpty);
        Assert.Equal(InlineContent.Empty, InlineContent.FromText(""));
    }

    [Fact]
    public void Constructor_MergesAdjacentTextAndDropsEmptyText()
    {
        var content = new InlineContent(
        [
            new InlineText("Hello"), new InlineText(" "), BoldStart, new InlineText(""), new InlineText("world"), BoldEnd,
        ]);

        Assert.Equal<InlineElement>([new InlineText("Hello "), BoldStart, new InlineText("world"), BoldEnd], content);
    }

    [Fact]
    public void Constructor_RejectsNullElements()
    {
        Assert.Throws<ArgumentException>(() => new InlineContent([new InlineText("a"), null!]));
    }

    [Fact]
    public void Equality_IsStructural()
    {
        var a = new InlineContent([new InlineText("Hello "), BoldStart, new InlineText("world"), BoldEnd]);
        var b = new InlineContent([new InlineText("Hel"), new InlineText("lo "), BoldStart with { }, new InlineText("world"), BoldEnd]);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.NotEqual(a, new InlineContent([new InlineText("Hello "), BoldStart with { SubType = "xlf:i" }, new InlineText("world"), BoldEnd]));
    }

    [Fact]
    public void Equality_ComparesExtensionAttributesByValue()
    {
        var a = new InlineCode("1", InlineCodeKind.Standalone) { ExtensionAttributes = [new("urn:x", "fs", "b")] };
        var b = new InlineCode("1", InlineCodeKind.Standalone) { ExtensionAttributes = [new("urn:x", "fs", "b")] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { ExtensionAttributes = [new("urn:x", "fs", "i")] });
    }

    [Fact]
    public void ExtensionAttributes_AreSnapshotted()
    {
        var attributes = new List<ExtensionAttr> { new("urn:x", "fs", "b") };
        var code = new InlineCode("1", InlineCodeKind.Standalone) { ExtensionAttributes = attributes };

        attributes.Add(new("urn:x", "fs", "i"));

        Assert.Single(code.ExtensionAttributes);
    }

    [Fact]
    public void ToPlainText_LeavesOutCodesAndMarkers()
    {
        var content = new InlineContent(
        [
            new InlineText("Press "), BoldStart, new InlineText("OK"), BoldEnd,
            new InlineCode("2", InlineCodeKind.Standalone) { Equiv = "\n" },
            new InlineMarker("m1", InlineMarkerKind.Opening) { Translate = false }, new InlineText("Contoso"), new InlineMarker("m1", InlineMarkerKind.Closing),
        ]);

        Assert.Equal("Press OKContoso", content.ToPlainText());
        Assert.True(content.HasTags);
        Assert.False(InlineContent.FromText("plain").HasTags);
    }

    [Fact]
    public void ToEquivText_ReplacesCodesWithTheirEquivalent()
    {
        var content = new InlineContent(
        [
            new InlineText("Line 1"), new InlineCode("1", InlineCodeKind.Standalone) { Equiv = "\n" }, new InlineText("Line 2"),
            new InlineCode("2", InlineCodeKind.Standalone), // no equivalent: contributes nothing
        ]);

        Assert.Equal("Line 1\nLine 2", content.ToEquivText());
    }
}
