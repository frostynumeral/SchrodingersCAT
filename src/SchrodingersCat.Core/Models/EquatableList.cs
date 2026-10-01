using System.Collections;

namespace SchrodingersCat.Core.Models;

/// <summary>
/// Read-only list snapshot with value equality, so records holding a list still compare by content.
/// </summary>
internal sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    public static readonly EquatableList<T> Empty = new([]);

    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(EquatableList<T>? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
            hash.Add(item);
        return hash.ToHashCode();
    }

    public override string ToString() => $"[{string.Join(", ", _items)}]";
}
