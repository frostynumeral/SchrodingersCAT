using System.Xml.Linq;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Tests.Properties;

/// <summary>
/// Turns a <see cref="DocumentPlan"/> into a valid model: unique ids, every pair closed within its unit side,
/// dataRefs that resolve, and only attribute combinations the reader can produce.
/// </summary>
internal static class DocumentBuilder
{
    public static TranslationDocument Build(DocumentPlan plan)
    {
        var document = new TranslationDocument { SourceLanguage = plan.SourceLanguage, TargetLanguage = plan.TargetLanguage };
        AddAttributes(document.ExtensionAttributes, plan.Attributes);

        for (var i = 0; i < plan.Files.Length; i++)
            document.Files.Add(BuildFile(plan.Files[i], $"f{i + 1}"));

        // trgLang is required as soon as there is a target.
        if (document.Units.Any(u => u.Segments.Any(s => s.Target is not null)))
            document.TargetLanguage ??= "hu";

        return document;
    }

    private static TranslationFile BuildFile(FilePlan plan, string id)
    {
        var file = new TranslationFile
        {
            Id = id,
            Original = plan.Original,
            CanResegment = plan.CanResegment,
            Translate = plan.Translate,
            SourceDir = plan.SourceDir,
            TargetDir = plan.TargetDir,
            Skeleton = plan.Skeleton is null ? null : new XElement(plan.Skeleton),
        };
        AddNotes(file.Notes, plan.Notes);
        file.ExtensionElements.AddRange(plan.Extensions.Select(e => new XElement(e)));
        AddAttributes(file.ExtensionAttributes, plan.Attributes);

        var ids = new FileIds();
        file.Items.AddRange(plan.Items.Select(item => BuildItem(item, ids)));
        return file;
    }

    private sealed class FileIds
    {
        public int Units { get; set; }

        public int Groups { get; set; }
    }

    private static TranslationItem BuildItem(ItemPlan plan, FileIds ids)
    {
        TranslationItem item = plan switch
        {
            UnitPlan unit => BuildUnit(unit, $"u{++ids.Units}"),
            GroupPlan group => BuildGroup(group, $"g{++ids.Groups}", ids),
            _ => throw new ArgumentOutOfRangeException(nameof(plan)),
        };

        var a = plan.Attributes;
        item.Name = a.Name;
        item.Type = a.Type;
        item.CanResegment = a.CanResegment;
        item.Translate = a.Translate;
        item.SourceDir = a.SourceDir;
        item.TargetDir = a.TargetDir;
        AddAttributes(item.ExtensionAttributes, a.Attributes);
        AddNotes(item.Notes, plan.Notes);
        item.ExtensionElements.AddRange(plan.Extensions.Select(e => new XElement(e)));
        return item;
    }

    private static TranslationGroup BuildGroup(GroupPlan plan, string id, FileIds ids)
    {
        var group = new TranslationGroup { Id = id };
        group.Items.AddRange(plan.Items.Select(item => BuildItem(item, ids)));
        return group;
    }

    private static TranslationUnit BuildUnit(UnitPlan plan, string id)
    {
        var unit = new TranslationUnit { Id = id };
        for (var i = 0; i < plan.Data.Length; i++)
            unit.OriginalData[$"d{i}"] = plan.Data[i];

        // A unit needs at least one segment that isn't ignorable.
        var segmentPlans = plan.Segments.All(s => s.Ignorable)
            ? [plan.Segments[0] with { Ignorable = false }, .. plan.Segments[1..]]
            : plan.Segments;

        var source = new SideBuilder(unit);
        var target = new SideBuilder(unit);
        var sources = new List<List<InlineElement>>();
        var targets = new List<List<InlineElement>?>();
        for (var i = 0; i < segmentPlans.Length; i++)
        {
            var s = segmentPlans[i];
            sources.Add(source.Build(s.Source));
            targets.Add(s.Target is null ? null : target.Build(s.Target));
        }

        source.CloseAll(sources[^1]);
        if (targets.LastOrDefault(t => t is not null) is { } lastTarget)
            target.CloseAll(lastTarget);

        // Explicit target orders: unique, between 1 and the number of segments and ignorables.
        var positions = Enumerable.Range(1, segmentPlans.Length).ToList();
        for (var i = 0; i < segmentPlans.Length; i++)
        {
            var s = segmentPlans[i];
            int? order = null;
            if (s.OrderKey is { } key && targets[i] is not null)
            {
                order = positions[key % positions.Count];
                positions.Remove(order.Value);
            }

            unit.Segments.Add(new Segment
            {
                Id = s.HasId ? $"s{i + 1}" : null,
                Source = new InlineContent(sources[i]),
                Target = targets[i] is { } t ? new InlineContent(t) : null,
                IsIgnorable = s.Ignorable,
                State = s.Ignorable ? SegmentState.Initial : s.State,
                SubState = s.Ignorable ? null : s.SubState,
                CanResegment = s.Ignorable ? null : s.CanResegment,
                TargetOrder = order,
            });
        }

        return unit;
    }

    /// <summary>Ids and open pairs for the sources or the targets of one unit (pairs may span segments).</summary>
    private sealed class SideBuilder(TranslationUnit unit)
    {
        private readonly List<(string Id, CodeAttrs Code)> _openCodes = [];
        private readonly List<string> _openMarkers = [];
        private int _codes;
        private int _markers;

        public List<InlineElement> Build(Token[] tokens)
        {
            var elements = new List<InlineElement>();
            foreach (var token in tokens)
            {
                switch (token)
                {
                    case TextToken text:
                        elements.Add(new InlineText(text.Text));
                        break;

                    case StandaloneToken standalone:
                        elements.Add(Code(NewCodeId(), InlineCodeKind.Standalone, standalone.Code) with { CanOverlap = null });
                        break;

                    case OpenToken open:
                        var id = NewCodeId();
                        _openCodes.Add((id, open.Code));
                        elements.Add(Code(id, InlineCodeKind.Opening, open.Code) with { Dir = open.Code.Dir });
                        break;

                    case CloseToken close when _openCodes.Count > 0:
                        var (openId, opening) = _openCodes[close.Pick % _openCodes.Count];
                        _openCodes.RemoveAt(close.Pick % _openCodes.Count);
                        elements.Add(Closing(openId, opening, close.MatchesOpening ? null : close.Code, close.Code));
                        break;

                    case IsolatedToken isolated:
                        var kind = isolated.IsOpening ? InlineCodeKind.Opening : InlineCodeKind.Closing;
                        elements.Add(Code(NewCodeId(), kind, isolated.Code) with { IsIsolated = true, Dir = isolated.Code.Dir });
                        break;

                    case MarkerOpenToken open:
                        var markerId = $"m{++_markers}";
                        _openMarkers.Add(markerId);
                        var m = open.Marker;
                        elements.Add(new InlineMarker(markerId, InlineMarkerKind.Opening)
                        {
                            Translate = m.Translate,
                            Type = m.Type,
                            Ref = m.Ref,
                            Value = m.Value,
                            ExtensionAttributes = Attributes(m.Attributes),
                        });
                        break;

                    case MarkerCloseToken close when _openMarkers.Count > 0:
                        var closed = _openMarkers[close.Pick % _openMarkers.Count];
                        _openMarkers.Remove(closed);
                        elements.Add(new InlineMarker(closed, InlineMarkerKind.Closing));
                        break;
                }
            }

            return elements;
        }

        /// <summary>Closes the pairs still open at the end of the unit, in the given (last) segment.</summary>
        public void CloseAll(List<InlineElement> last)
        {
            foreach (var (id, opening) in _openCodes)
                last.Add(Closing(id, opening, null, opening));
            foreach (var id in _openMarkers)
                last.Add(new InlineMarker(id, InlineMarkerKind.Closing));
            _openCodes.Clear();
            _openMarkers.Clear();
        }

        private string NewCodeId() => $"c{++_codes}";

        // With pairLevel null the closing half repeats the opening's pair-level attributes, so it can be written as
        // <pc>; otherwise it has its own and is written as <sc>/<ec>. A non-isolated <ec> can't carry dir.
        private InlineCode Closing(string id, CodeAttrs opening, CodeAttrs? pairLevel, CodeAttrs own)
        {
            var source = pairLevel ?? opening;
            var canReorder = pairLevel is null && opening.CanReorder == CanReorder.FirstNo ? CanReorder.No : source.CanReorder;
            return Code(id, InlineCodeKind.Closing, source with
            {
                CanReorder = canReorder,
                DataRef = own.DataRef,
                Disp = own.Disp,
                Equiv = own.Equiv,
                SubFlows = own.SubFlows,
                Attributes = pairLevel is null ? [] : own.Attributes,
            });
        }

        // Paired codes always have CanOverlap set, because the reader resolves it to the element's default.
        private InlineCode Code(string id, InlineCodeKind kind, CodeAttrs c) => new(id, kind)
        {
            Type = c.Type,
            SubType = c.SubType,
            CopyOf = c.CopyOf,
            CanCopy = c.CanCopy,
            CanDelete = c.CanDelete,
            CanReorder = c.CanReorder,
            CanOverlap = c.CanOverlap,
            DataRef = c.DataRef is { } d ? $"d{d % unit.OriginalData.Count}" : null,
            Disp = c.Disp,
            Equiv = c.Equiv,
            SubFlows = c.SubFlows,
            ExtensionAttributes = Attributes(c.Attributes),
        };
    }

    private static void AddNotes(List<Note> notes, NotePlan[] plans)
    {
        for (var i = 0; i < plans.Length; i++)
        {
            var p = plans[i];
            var note = new Note
            {
                Id = p.HasId ? $"n{i + 1}" : null,
                Text = p.Text,
                Category = p.Category,
                Priority = p.Priority,
                AppliesTo = p.AppliesTo,
            };
            AddAttributes(note.ExtensionAttributes, p.Attributes);
            notes.Add(note);
        }
    }

    private static void AddAttributes(List<ExtensionAttr> target, ExtensionAttrPlan[] plans) =>
        target.AddRange(Attributes(plans));

    // An element can't carry the same attribute twice.
    private static List<ExtensionAttr> Attributes(ExtensionAttrPlan[] plans) =>
        plans.Select(p => new ExtensionAttr(XliffGen.AttributeNamespaces[p.Namespace], XliffGen.AttributeNames[p.Name], p.Value))
            .DistinctBy(a => (a.Namespace, a.LocalName))
            .ToList();
}
