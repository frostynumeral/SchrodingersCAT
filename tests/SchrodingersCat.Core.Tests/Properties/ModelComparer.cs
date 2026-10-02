using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests.Properties;

/// <summary>
/// Deep comparison of two models through every public property, found by reflection, so properties added to the
/// model later are compared without anyone remembering to add them here.
/// </summary>
internal static class ModelComparer
{
    /// <summary>The path and description of the first difference, or null if the models are equal.</summary>
    public static string? FindDifference(object? expected, object? actual) => Compare(expected, actual, "document");

    private static string? Compare(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null ? null : Describe(path, expected, actual);
        if (expected.GetType() != actual.GetType())
            return $"{path}: expected a {expected.GetType().Name}, got a {actual.GetType().Name}";

        switch (expected)
        {
            case string or ValueType or ExtensionAttr:
                return Equals(expected, actual) ? null : Describe(path, expected, actual);

            case XElement element:
                return XNode.DeepEquals(Normalize(element), Normalize((XElement)actual)) ? null : Describe(path, expected, actual);

            case IDictionary dictionary:
                var other = (IDictionary)actual;
                if (dictionary.Count != other.Count)
                    return $"{path}: expected {dictionary.Count} entries, got {other.Count}";
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!other.Contains(entry.Key))
                        return $"{path}[{entry.Key}]: missing";
                    if (Compare(entry.Value, other[entry.Key], $"{path}[{entry.Key}]") is { } difference)
                        return difference;
                }

                return null;

            case IEnumerable sequence:
                var expectedItems = sequence.Cast<object?>().ToList();
                var actualItems = ((IEnumerable)actual).Cast<object?>().ToList();
                if (expectedItems.Count != actualItems.Count)
                    return $"{path}: expected {expectedItems.Count} items, got {actualItems.Count}";
                for (var i = 0; i < expectedItems.Count; i++)
                {
                    if (Compare(expectedItems[i], actualItems[i], $"{path}[{i}]") is { } difference)
                        return difference;
                }

                return null;

            default:
                foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0)
                        continue;
                    if (Compare(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}") is { } difference)
                        return difference;
                }

                return null;
        }
    }

    // Namespace prefixes carry no data, and a verbatim element read back declares the ones it uses.
    private static XElement Normalize(XElement element)
    {
        var copy = new XElement(element);
        copy.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        return copy;
    }

    private static string Describe(string path, object? expected, object? actual) =>
        $"{path}: expected {Show(expected)}, got {Show(actual)}";

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => Escape(s),
        _ => Escape(value.ToString() ?? ""),
    };

    // Makes whitespace and invisible characters visible in failure messages.
    private static string Escape(string text) =>
        "\"" + string.Concat(text.Select(c => c is '\t' or '\n' or '\r' || char.IsControl(c) || char.IsSurrogate(c) || c > '~' && char.IsWhiteSpace(c)
            ? $"\\u{(int)c:X4}"
            : c.ToString())) + "\"";
}
