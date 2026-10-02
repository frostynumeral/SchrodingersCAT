using System.Xml;

namespace SchrodingersCat.Core.Xliff;

/// <summary>
/// Passes an <see cref="XmlReader"/> through, but stops as soon as an element is nested deeper than the limit.
/// </summary>
/// <remarks>
/// Checking the finished tree would be too late: building an <c>XDocument</c> gets quadratically slower with depth,
/// so a small file nested 100,000 deep costs tens of seconds before any check could run.
/// </remarks>
internal sealed class DepthLimitingXmlReader(XmlReader inner, int maxDepth) : XmlReader
{
    public override int AttributeCount => inner.AttributeCount;

    public override string BaseURI => inner.BaseURI;

    public override int Depth => inner.Depth;

    public override bool EOF => inner.EOF;

    public override bool HasValue => inner.HasValue;

    public override bool IsDefault => inner.IsDefault;

    public override bool IsEmptyElement => inner.IsEmptyElement;

    public override string LocalName => inner.LocalName;

    public override string NamespaceURI => inner.NamespaceURI;

    public override XmlNameTable NameTable => inner.NameTable;

    public override XmlNodeType NodeType => inner.NodeType;

    public override string Prefix => inner.Prefix;

    public override ReadState ReadState => inner.ReadState;

    public override XmlReaderSettings? Settings => inner.Settings;

    public override string Value => inner.Value;

    public override string XmlLang => inner.XmlLang;

    public override XmlSpace XmlSpace => inner.XmlSpace;

    public override string GetAttribute(int i) => inner.GetAttribute(i);

    public override string? GetAttribute(string name) => inner.GetAttribute(name);

    public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);

    public override Task<string> GetValueAsync() => inner.GetValueAsync();

    public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);

    public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);

    public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);

    public override bool MoveToElement() => inner.MoveToElement();

    public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();

    public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();

    public override bool Read() => Check(inner.Read());

    public override async Task<bool> ReadAsync() => Check(await inner.ReadAsync().ConfigureAwait(false));

    public override bool ReadAttributeValue() => inner.ReadAttributeValue();

    public override void ResolveEntity() => inner.ResolveEntity();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    // XmlReader counts the root element as depth 0; the limit counts it as 1.
    private bool Check(bool read)
    {
        if (read && inner.NodeType == XmlNodeType.Element && inner.Depth >= maxDepth)
            throw new XliffFormatException($"The file nests elements more than {maxDepth} deep (at <{inner.LocalName}>).");
        return read;
    }
}
