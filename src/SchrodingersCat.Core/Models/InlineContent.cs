using System.Collections;
using System.Text;

namespace SchrodingersCat.Core.Models;

/// <summary>
/// The content of a segment's source or target: an immutable, flat sequence of <see cref="InlineElement"/>s.
/// </summary>
/// <remarks>
/// Immutable so that an edit is a new value: undo keeps old values, and "has this changed" is an equality check.
/// Adjacent text is merged and empty text dropped, so equal content always has the same shape.
/// There is deliberately no implicit conversion from <see cref="string"/>: it would hide where codes get lost.
/// </remarks>
public sealed class InlineContent : IReadOnlyList<InlineElement>, IEquatable<InlineContent>
{
    public static InlineContent Empty { get; } = new([]);

    private readonly InlineElement[] _elements;

    public InlineContent(IEnumerable<InlineElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var result = new List<InlineElement>();
        StringBuilder? pendingText = null;

        foreach (var element in elements)
        {
            switch (element)
            {
                case null:
                    throw new ArgumentException("Content cannot contain null elements.", nameof(elements));
                case InlineText text:
                    (pendingText ??= new StringBuilder()).Append(text.Text);
                    break;
                default:
                    FlushText();
                    result.Add(element);
                    break;
            }
        }

        FlushText();
        _elements = result.ToArray();

        void FlushText()
        {
            if (pendingText is { Length: > 0 })
                result.Add(new InlineText(pendingText.ToString()));
            pendingText = null;
        }
    }

    public static InlineContent FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? Empty : new InlineContent([new InlineText(text)]);
    }

    public int Count => _elements.Length;

    public InlineElement this[int index] => _elements[index];

    public bool IsEmpty => _elements.Length == 0;

    /// <summary><c>true</c> if the content contains any codes or markers, not just text.</summary>
    public bool HasTags => _elements.Any(e => e is not InlineText);

    /// <summary>The text only; codes and markers are left out.</summary>
    public string ToPlainText() => string.Concat(_elements.OfType<InlineText>().Select(t => t.Text));

    /// <summary>The text with each code replaced by its <see cref="InlineCode.Equiv"/>; for search, word counts and MT.</summary>
    public string ToEquivText() => string.Concat(_elements.Select(e => e switch
    {
        InlineText text => text.Text,
        InlineCode code => code.Equiv,
        _ => null,
    }));

    public IEnumerator<InlineElement> GetEnumerator() => ((IEnumerable<InlineElement>)_elements).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(InlineContent? other) =>
        other is not null && _elements.AsSpan().SequenceEqual(other._elements);

    public override bool Equals(object? obj) => Equals(obj as InlineContent);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var element in _elements)
            hash.Add(element);
        return hash.ToHashCode();
    }

    public static bool operator ==(InlineContent? left, InlineContent? right) => Equals(left, right);

    public static bool operator !=(InlineContent? left, InlineContent? right) => !Equals(left, right);

    /// <summary>Lists the elements, so test failures and the debugger show the codes too.</summary>
    public override string ToString() => $"[{string.Join(", ", _elements)}]";
}
