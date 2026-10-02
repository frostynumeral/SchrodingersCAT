namespace SchrodingersCat.Core.Tests.Properties;

/// <summary>
/// A deterministic source of choices for the mutators, fed from a generated int array, so CsCheck can shrink and
/// replay a mutation like any other generated value.
/// </summary>
internal sealed class Choices(int[] values)
{
    private int _next;

    /// <summary>A number in [0, count), or 0 when count is 0.</summary>
    public int Next(int count) =>
        count <= 0 || values.Length == 0 ? 0 : (int)((uint)values[_next++ % values.Length] % (uint)count);

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
}
