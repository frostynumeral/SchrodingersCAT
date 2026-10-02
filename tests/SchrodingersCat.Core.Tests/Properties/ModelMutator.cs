using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using SchrodingersCat.Core.Models;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Core.Tests.Properties;

/// <summary>
/// Breaks a model in random ways the writer has to cope with: any settable property (found by reflection) gets a
/// hostile value, lists lose, repeat or gain entries (including null), and inline content is reshuffled.
/// </summary>
internal static class ModelMutator
{
    private static readonly string?[] Strings =
        [null, "", " ", "x", "\u0001", "\uD800", "a\rb", "not a name", "ltr", "up", "yes", "x:y", "c1", "m1", "d0", "s1", "u1", "g1", "n1", "f1"];

    private static readonly int?[] Numbers = [null, 0, -1, 1, 2, 3, 10, 11, int.MaxValue];

    private static readonly XElement?[] Elements =
    [
        null,
        new(XName.Get("skeleton", XliffGen.Xliff)),
        new(XName.Get("skeleton", XliffGen.Xliff), new XAttribute("href", "a.skl")),
        new(XName.Get("unit", XliffGen.Xliff)),
        new("noNamespace"),
        new(XName.Get("ext", "urn:example:tool"), "\u0001"),
        new(XName.Get("ext", "urn:example:tool"), new XComment("a--b")),
        new(XName.Get("ext", "urn:example:tool"), new XComment("a\rb")),
        new(XName.Get("ext", "urn:example:tool"), new XAttribute("a", "ok")),
    ];

    private static readonly ExtensionAttr?[] Attributes =
    [
        null,
        new("urn:example:tool", "a", "1"),
        new("urn:example:tool", "not a name", "1"),
        new(XliffGen.Xliff, "id", "1"),
        new("", "plain", "1"),
        new("http://www.w3.org/XML/1998/namespace", "lang", "en"),
        new("urn:example:tool", "a", "\u0002"),
        new("urn:example:tool", "a", "\r\n\t"),
    ];

    public static void Mutate(TranslationDocument document, Choices choices)
    {
        var count = 1 + choices.Next(3);
        for (var i = 0; i < count; i++)
        {
            var slots = new List<Slot>();
            Collect(document, slots, new HashSet<object>(ReferenceEqualityComparer.Instance));
            choices.Pick(slots).Mutate(choices);
        }
    }

    private abstract record Slot
    {
        public abstract void Mutate(Choices choices);
    }

    private sealed record PropertySlot(object Owner, PropertyInfo Property) : Slot
    {
        public override void Mutate(Choices choices)
        {
            var type = Property.PropertyType;
            var current = Property.GetValue(Owner);
            object? value = type == typeof(InlineContent)
                ? choices.Next(4) == 0 ? null : MutateContent((InlineContent?)current ?? InlineContent.Empty, choices)
                : RandomValue(type, current, choices);
            Property.SetValue(Owner, value);
        }
    }

    private sealed record ListSlot(IList List, Type ItemType) : Slot
    {
        public override void Mutate(Choices choices)
        {
            switch (choices.Next(6))
            {
                case 0 when List.Count > 0:
                    List.RemoveAt(choices.Next(List.Count));
                    break;
                case 1 when List.Count > 0:
                    List.Insert(choices.Next(List.Count + 1), List[choices.Next(List.Count)]);
                    break;
                case 2:
                    List.Insert(choices.Next(List.Count + 1), null);
                    break;
                case 3:
                    List.Clear();
                    break;
                case 4 when List.Count > 0 && ItemType == typeof(ExtensionAttr):
                    var index = choices.Next(List.Count);
                    if (List[index] is { } record)
                        List[index] = MutateRecord(record, choices);
                    break;
                default:
                    List.Insert(choices.Next(List.Count + 1), NewItem(ItemType, choices));
                    break;
            }
        }
    }

    private sealed record DictionarySlot(Dictionary<string, string> Dictionary) : Slot
    {
        public override void Mutate(Choices choices)
        {
            var keys = Dictionary.Keys.ToList();
            switch (choices.Next(3))
            {
                case 0 when keys.Count > 0:
                    Dictionary.Remove(choices.Pick(keys));
                    break;
                case 1 when keys.Count > 0:
                    Dictionary[choices.Pick(keys)] = choices.Pick(Strings)!;
                    break;
                default:
                    Dictionary[choices.Pick(Strings) ?? "d9"] = choices.Pick(Strings)!;
                    break;
            }
        }
    }

    // Walks the model's own classes; computed views (Units, TranslatableSegments, …) are not lists and are skipped.
    private static void Collect(object node, List<Slot> slots, HashSet<object> seen)
    {
        if (!seen.Add(node))
            return;

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            // Computed properties (Source, Units, …) may throw on a model that is already broken; only read the rest.
            var settable = property.SetMethod?.IsPublic == true;
            var type = property.PropertyType;
            var isCollection = type == typeof(Dictionary<string, string>) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
            if (settable)
                slots.Add(new PropertySlot(node, property));
            if (!isCollection)
                continue;

            var value = property.GetValue(node);

            switch (value)
            {
                case Dictionary<string, string> dictionary:
                    slots.Add(new DictionarySlot(dictionary));
                    break;
                case IList list when property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(List<>):
                    var itemType = property.PropertyType.GetGenericArguments()[0];
                    slots.Add(new ListSlot(list, itemType));
                    foreach (var item in list)
                    {
                        if (item is not null && IsModelClass(item.GetType()))
                            Collect(item, slots, seen);
                    }

                    break;
            }
        }
    }

    private static bool IsModelClass(Type type) =>
        type.Namespace == typeof(TranslationDocument).Namespace && type.IsClass && type != typeof(InlineContent)
        && type != typeof(ExtensionAttr) && !type.IsSubclassOf(typeof(InlineElement));

    private static object? RandomValue(Type type, object? current, Choices choices)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = underlying is not null || !type.IsValueType;
        var valueType = underlying ?? type;

        if (valueType == typeof(string))
            return choices.Pick(Strings);
        if (valueType == typeof(bool))
            return nullable && choices.Next(3) == 0 ? null : current is bool b ? !b : choices.Next(2) == 0;
        if (valueType == typeof(int))
            return choices.Pick(Numbers) ?? (nullable ? null : 0);
        if (valueType.IsEnum)
        {
            var values = Enum.GetValues(valueType).Cast<object>().Append(Enum.ToObject(valueType, 42)).Append(Enum.ToObject(valueType, -1)).ToList();
            return nullable && choices.Next(values.Count + 1) == 0 ? null : choices.Pick(values);
        }

        if (valueType == typeof(XElement))
            return choices.Pick(Elements) is { } element ? new XElement(element) : null;
        if (typeof(IEnumerable<ExtensionAttr>).IsAssignableFrom(valueType))
            return choices.Pick(Attributes) is { } attribute ? new List<ExtensionAttr> { attribute } : new List<ExtensionAttr>();

        return current;
    }

    private static object? NewItem(Type type, Choices choices)
    {
        if (type == typeof(XElement))
            return choices.Pick(Elements) is { } element ? new XElement(element) : null;
        if (type == typeof(ExtensionAttr))
            return choices.Pick(Attributes);
        if (type == typeof(Note))
            return new Note { Id = choices.Pick(Strings), Text = "x" };
        if (type == typeof(Segment))
            return new Segment { Id = choices.Pick(Strings), Source = InlineContent.FromText("x") };
        if (type == typeof(TranslationFile))
            return new TranslationFile { Id = choices.Pick(Strings)! };
        if (type == typeof(TranslationItem))
        {
            return choices.Next(2) == 0
                ? new TranslationGroup { Id = choices.Pick(Strings)! }
                : new TranslationUnit { Id = choices.Pick(Strings)!, Segments = { new Segment { Source = InlineContent.FromText("x") } } };
        }

        return null;
    }

    private static InlineContent MutateContent(InlineContent content, Choices choices)
    {
        var elements = content.ToList();
        switch (choices.Next(5))
        {
            case 0 when elements.Count > 0:
                elements.RemoveAt(choices.Next(elements.Count));
                break;
            case 1 when elements.Count > 0:
                elements.Insert(choices.Next(elements.Count + 1), elements[choices.Next(elements.Count)]);
                break;
            case 2 when elements.Count > 1:
                var (i, j) = (choices.Next(elements.Count), choices.Next(elements.Count));
                (elements[i], elements[j]) = (elements[j], elements[i]);
                break;
            case 3 when elements.Count > 0:
                var index = choices.Next(elements.Count);
                elements[index] = (InlineElement)MutateRecord(elements[index], choices);
                break;
            default:
                elements.Insert(choices.Next(elements.Count + 1), choices.Next(4) switch
                {
                    0 => new InlineText(choices.Pick(Strings) ?? "x"),
                    1 => new InlineCode(choices.Pick(Strings) ?? "c1", (InlineCodeKind)choices.Next(4)),
                    2 => new InlineMarker(choices.Pick(Strings) ?? "m1", (InlineMarkerKind)choices.Next(3)),
                    _ => new InlineText("\u0001\r"),
                });
                break;
        }

        return new InlineContent(elements);
    }

    // Records are immutable: clone through the compiler-generated <Clone>$ and set one init-only property on the copy.
    private static object MutateRecord(object record, Choices choices)
    {
        var properties = record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null && p.Name != "EqualityContract")
            .ToList();
        if (properties.Count == 0)
            return record;

        var copy = record.GetType().GetMethod("<Clone>$")!.Invoke(record, null)!;
        var property = choices.Pick(properties);
        var value = RandomValue(property.PropertyType, property.GetValue(copy), choices);
        if (value is not null || !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null)
        {
            // Required constructor arguments (Id, Text, …) reject null; keep the original value then.
            try
            {
                property.SetValue(copy, value);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentNullException)
            {
            }
        }

        return copy;
    }
}
