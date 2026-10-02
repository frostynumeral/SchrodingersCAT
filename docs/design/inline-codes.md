# Proposal: inline codes

**Status:** implemented (accepted 2026-09-29, all steps done 2026-10-01).

## Problem

`Segment.Source`/`Target` are plain strings, so `Xliff20Serializer` rejects any segment that contains inline elements. Most real XLIFF files contain them (bold/italic, links, `{0}` placeholders, line breaks), so SchrödingersCAT can't open most real files yet.

XLIFF 2.0 inline content is made up of:

| Element | Meaning |
|---|---|
| `<ph/>` | Standalone code (placeholder, `<br/>`, `{0}`) |
| `<pc>…</pc>` | Paired code wrapping text (`<b>…</b>`) |
| `<sc/>` … `<ec/>` | The same pair, written as two separate markers. Required when the pair isn't well-formed, spans segments, or has its other end outside the unit (`isolated="yes"`) |
| `<mrk>…</mrk>` | Annotation (`translate="no"`, term, comment) |
| `<sm/>` … `<em/>` | The same annotation, written as separate markers (for overlap or spanning segments) |
| `<cp hex="…"/>` | A character that is invalid in XML 1.0 |
| `<originalData>` | Unit-level map of `id → native code` (e.g. `<b>`), referenced via `dataRef` |

## Proposed model

### 1. A flat list of runs, not a tree

A segment's content becomes a flat sequence: text, standalone codes, opening tags and closing tags. Nesting is implied by matching ids, rather than by an XML-like tree.

This means `<pc>` and `<sc>/<ec>` load into **the same** model. The same goes for `<mrk>` and `<sm>/<em>`. The reasons:

- Pairs can span segments. `<pc>` can't, `<sc>/<ec>` can, and a flat list represents both identically.
- The editor works on a flat sequence anyway: a text box with tag "chips".
- Segment split/merge (later) doesn't need to restructure a tree.
- `<pc>` attributes already split into start and end halves (`dataRefStart`/`dataRefEnd`, `dispStart`/`dispEnd`, `equivStart`/`equivEnd`, `subFlowsStart`/`subFlowsEnd`). They map directly onto the opening and closing runs.

```csharp
public abstract record InlineElement;

public sealed record InlineText(string Text) : InlineElement;

public enum InlineCodeKind { Standalone, Opening, Closing }

/// ph / pc / sc / ec. Id is shared by the opening and closing halves of a pair.
public sealed record InlineCode(string Id, InlineCodeKind Kind) : InlineElement
{
    public string? DataRef { get; init; }   // key into TranslationUnit.OriginalData
    public string? Type { get; init; }      // fmt, ui, quote, link, image, other
    public string? SubType { get; init; }   // e.g. xlf:b
    public string? Disp { get; init; }      // what the editor shows on the chip
    public string? Equiv { get; init; }     // plain-text equivalent (search, MT, word count)
    public string? CopyOf { get; init; }
    public string? SubFlows { get; init; }
    public string? Dir { get; init; }
    public bool CanCopy { get; init; } = true;
    public bool CanDelete { get; init; } = true;
    public CanReorder CanReorder { get; init; } = CanReorder.Yes;   // yes | firstNo | no
    public bool? CanOverlap { get; init; }
    public bool IsIsolated { get; init; }   // other half is outside this unit
    public IReadOnlyList<ExtensionAttr> ExtensionAttributes { get; init; } = [];
}

public enum InlineMarkerKind { Opening, Closing }

/// mrk / sm / em.
public sealed record InlineMarker(string Id, InlineMarkerKind Kind) : InlineElement
{
    public bool? Translate { get; init; }   // translate="no" -> the editor locks the span
    public string? Type { get; init; }      // generic, comment, term, or prefix:value
    public string? Ref { get; init; }
    public string? Value { get; init; }
    public IReadOnlyList<ExtensionAttr> ExtensionAttributes { get; init; } = [];
}

public sealed record ExtensionAttr(string Namespace, string LocalName, string Value);
```

`<cp>` gets **no** model type. The reader decodes it into the actual character inside `InlineText`. The writer encodes any XML-invalid character back into `<cp hex>` (XLinq would refuse to write it otherwise).

`ExtensionAttributes` keeps attributes from other namespaces as-is, e.g. the `fs:`/`slr:` module attributes on codes. This follows the existing "never silently drop" rule without having to understand each module.

### 2. An immutable content value

```csharp
public sealed class InlineContent : IReadOnlyList<InlineElement>, IEquatable<InlineContent>
{
    public static InlineContent Empty { get; }
    public static InlineContent FromText(string text);
    public InlineContent(IEnumerable<InlineElement> elements);   // merges adjacent text

    public string ToPlainText();    // text only
    public string ToEquivText();    // text + each code's Equiv; for search, MT, word count
}
```

- `Segment.Source` becomes `required InlineContent Source`, and `Target` becomes `InlineContent? Target`. The existing `null` vs empty distinction is kept.
- Content is immutable, and editing assigns a new value. That makes undo/redo a matter of keeping old values, and "has this segment changed?" becomes a simple equality check.
- There is no implicit `string → InlineContent` conversion. It would hide exactly the place where codes get lost. Tests and callers use `InlineContent.FromText`.
- `TranslationUnit.Source`/`Target` become `InlineContent` too, concatenating the segments' content so codes aren't lost. Callers that want text use `ToPlainText()`.

### 3. `TranslationUnit.OriginalData`

A new `Dictionary<string, string> OriginalData` holds the native code for each `dataRef`, with `<cp>` decoded. It's stored per unit because that's where XLIFF anchors it.

## Reading

- Parse the elements above into the flat list. `<pc>` becomes opening, content, closing; `<mrk>` likewise.
- XLIFF requires a non-isolated `<ec>` to repeat its `<sc>`'s `canCopy`/`canDelete`/`canOverlap`/`canReorder`, and lets it carry its own `type`/`subType`. So a closing half repeats the pair-level attributes, and the reader copies `<pc>`'s onto both halves. `<sc>`/`<ec>` are read exactly as written. A `<pc>` and an `<sc>`/`<ec>` that carry the same information therefore load identically. When they don't, the model keeps the difference, and the writer falls back to `<sc>`/`<ec>`.
- `canOverlap` defaults differ (`no` for `<pc>`, `yes` for `<sc>`/`<ec>`), so the reader stores the resolved value and normalising can't silently change it.
- Validation, which fails loudly with `XliffFormatException`:
  - inline ids are unique within the unit's sources, and separately within its targets (target codes reuse their source code's id), and don't clash with segment or ignorable ids;
  - every `dataRef` exists in `<originalData>`;
  - every non-isolated `<sc>`/`<sm>` has its `<ec>`/`<em>` later in the same unit, and vice versa;
  - unknown elements or unqualified attributes in content are rejected; attributes from other namespaces go into `ExtensionAttributes`.
- The same logic applies to `<ignorable>`, which may contain codes too.

## Writing

For each opening/closing pair within one segment's content:

- If both halves are in this segment and properly nested, write `<pc>` / `<mrk>`.
- Otherwise write `<sc>`/`<ec>` or `<sm>`/`<em>`: with `startRef` if the other half is elsewhere in the unit, or `isolated="yes"` if it's outside the unit.

The spec treats both forms as equivalent. See decision 1.

## Out of scope

- Editor UI for tags (chips, inserting tags into the target).
- Tag QA: missing or extra tags, `canDelete="no"`, `canReorder` order.
- Resolving `subFlows` or `mrk` comment `ref`s to notes. The values are kept verbatim.
- Segment split/merge.

The model above is meant to make all of these possible without another rewrite.

## Implementation steps

1. Model types plus `InlineContent`, with unit tests (equality, text merging, plain/equiv text).
2. Switch `Segment`/`TranslationUnit` to `InlineContent`; update existing tests and the README example.
3. Reader: inline elements, `<cp>`, `<originalData>`, validation.
4. Writer: the pairing algorithm and `<cp>` encoding.
5. Round-trip tests using the examples from the XLIFF 2.0 spec (pc/sc-ec equivalence, spanning segments, isolated codes, nested `mrk`, `cp`), and remove the "inline codes rejected" limitation from the docs.

## Decisions

1. **Normalise or keep the original form?** When a pair is well-formed, the writer outputs `<pc>` even if the file had `<sc>/<ec>`. That's equivalent per the spec, but it creates diffs when files are kept in version control. The alternative is to remember the original form on each run. It costs a flag, but the writer then has to handle "original form no longer possible after editing".
   **Decision:** normalise now, and add form tracking only if diffs become a real problem.
2. **Include `<mrk>` / `<sm>/<em>` in this step?** It's the same machinery as codes, and `translate="no"` matters for the editor.
   **Decision:** yes.
