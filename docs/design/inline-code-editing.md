# Proposal: editing targets with inline codes

**Status:** accepted (2026-10-02). All [open questions](#open-questions) are decided; implementation follows the [steps](#implementation-steps), starting with step 1.

## Problem

Core reads and writes inline codes losslessly (see [inline-codes.md](inline-codes.md)), but the editor can't edit around them. `SegmentViewModel` makes every segment whose source or target contains a code or marker read-only, because its target is a plain `TextBox` string and editing it as text would drop the codes. In real files that's most segments: formatting (`<b>…</b>`), links, line breaks and placeholders (`{0}`, `%s`) are everywhere.

Translators need to:

- see where the codes are in the source, and what each one stands for;
- put the codes into the target at the right places, usually in a different order and around different words;
- leave out a code the translation doesn't need, or repeat one it needs twice, where the file allows it;
- be told when the target's codes don't match the source's, before the file goes back to the client.

## What XLIFF requires

The relevant rules from the XLIFF 2.0 specification (Editing Hints, 4.7.2.4–4.7.2.5, and the attribute definitions). The editor must respect them, because the file goes back to a tool that merges the translation into the original format.

| Rule | Source in the spec |
|---|---|
| A target code that corresponds to a source code uses **the same `id`**. | `copyOf`/target content rules |
| A code with `canDelete="no"` **must not be deleted** from the target. Others may be left out. | 4.7.2.5, Editing Hints |
| A code with `canCopy="no"` **must not be duplicated**. A duplicate gets a **new id**, reuses the base code's `dataRef`, and carries `copyOf="<base id>"` only if the base has no original data. | 4.7.2.4.1 |
| A **non-reorderable sequence** (one `canReorder="firstNo"` code followed by `canReorder="no"` codes) must keep its number and order of codes. The whole sequence may move relative to other codes and sequences. Such codes must also have `canCopy="no"` and `canDelete="no"`. | Editing Hints |
| Brand-new codes (new native markup, with new `<originalData>`) are allowed. | 4.7.2.4.2 |
| Annotations (`<mrk>`, `<sm>/<em>`) carry metadata (`translate="no"`, terms, comments). There are no rules about keeping them in the target. | 4.7.3 |

Two consequences for the design:

1. **Pairs can span segments.** A `<pc>` opened in one segment's target may close in the next one (`<sc>`/`<ec>`). Code checks therefore run per **unit**, not per segment, even though the editor shows them per segment.
2. **Core doesn't check the target against the source.** The reader and writer validate each side on its own (unique ids, closed pairs, existing `dataRef`s), but neither knows whether the target's codes match the source's. That check is new, and it belongs in Core because it's domain logic, not UI.

## Proposed design

The design has four layers, each testable on its own:

1. **Code check** (`Core`): compares a unit's target codes with its source codes and reports problems.
2. **Target edit model** (`App`, no Avalonia types): the target as an editable sequence of text and *code tokens*, with the editing operations (insert code, delete, copy source, wrap selection), the XLIFF rules and undo.
3. **Editor control** (`App`, Avalonia): shows the edit model with codes as chips, and turns keys and clicks into edit-model operations.
4. **Integration**: the segment list, the code check results, and saving.

Layers 1 and 2 hold all the rules, so the choice of control in layer 3 is replaceable. That matters, because it's the riskiest part (see [editor approaches](#editor-approaches)).

### 1. How codes are shown

Each code half is a **chip**: a small rounded label inside the text, never split by the caret or by selection.

- **Shape:** an opening half points right (`⟨b`), a closing half points left (`b⟩`), and a standalone code has round ends. The shape shows pairing without reading the label.
- **Label** (short by default):
  1. `Disp` if the code has one (`{0}`);
  2. otherwise a name from `SubType`/`Type` (`xlf:b` → `b`, `xlf:i` → `i`, `xlf:lb` → `↵`, `fmt` → the id);
  3. otherwise the id.
- **Tooltip:** the full native code from `OriginalData` (`<b class="warn">`), `Equiv`, and the editing hints in words ("Can't be deleted", "Can't be copied", "Part of a fixed-order sequence").
- **State colours:** neutral when fine; red outline when the code check reports a problem with it (missing partner, can't be copied, out of order); a lock glyph for `canDelete="no"`.
- **Annotations** (`<mrk>`) aren't chips. They're shown as a span style, like a dotted underline for terms or a grey background for `translate="no"`. In the target they behave like a pair of very small brackets that can be moved and deleted; see [open question 5](#open-questions).
- **The source column** uses the same chips, read-only. Clicking a source chip inserts that code at the target's caret.

The existing `InlineFormatter` placeholder text (`⟨1⟩OK⟨/1⟩`) stays for plain-text uses (copying as text, search, tooltips).

### 2. Putting codes into the target

| Action | How | Rule handling |
|---|---|---|
| **Copy source** | `Ctrl+Insert`, or a button | Replaces the target with the source text and codes (same ids). The usual start for segments that are mostly codes. Asks first if the target isn't empty. |
| **Insert next code** | `F9` | Inserts, at the caret, the first source code (in source order) that isn't in the target yet. A missing pair inserts both halves, with the caret between them. |
| **Wrap selection** | `F9` with a selection | If the next missing code is a pair, its halves go around the selection. |
| **Insert a specific code** | Click its chip in the source, or `Ctrl+Shift+F9` for a picker with type-ahead | Same as above, for any code. |
| **Duplicate a code** | Insert a code that's already in the target | Only if `canCopy="yes"`. Creates a new id and reuses `dataRef` (or sets `copyOf` when there is no `dataRef`). Refused with a status message otherwise. |
| **Move a code** | Cut and paste, or drag a chip | Moving a code inside a non-reorderable sequence is refused. Moving the whole sequence is allowed. |
| **Delete a code** | `Backspace`/`Delete` next to a chip removes the whole chip | Refused for `canDelete="no"`: the chip flashes and a status message explains. Deleting one half of a pair leaves the other; the check flags it until fixed or undone ([open question 4](#open-questions)). |
| **Delete a selection** | Containing chips | The text goes; protected chips stay, collected at the selection's position. |
| **Paste** | From inside the editor or the source | Keeps chips (own clipboard format, plus plain text with placeholders for other apps). From outside the app, pastes plain text only. |

Creating brand-new codes is **out of scope**: the translator would have to type native markup that the merging tool then trusts. See [open question 9](#open-questions).

### 3. Detecting missing, extra and reordered codes

A new Core class, `InlineCodeChecker`, checks one unit and returns issues tied to the segment (and code) they concern:

| Issue | When | Severity |
|---|---|---|
| **Missing (protected)** | A source code with `canDelete="no"` isn't in the target | Error |
| **Missing** | A deletable source code isn't in the target | Warning (often intentional, often a mistake) |
| **Unknown code** | A target code whose id isn't in the source and that isn't a valid copy (`copyOf` pointing at a source code, or a reused `dataRef`) | Error |
| **Copy not allowed** | A copy of a code with `canCopy="no"` | Error |
| **Unpaired** | An opening half without its closing half in the unit's target, or the reverse (isolated codes excepted) | Error (the writer would refuse the file anyway) |
| **Sequence broken** | The codes of a non-reorderable sequence are not all present, in their original order | Error |
| **Reordered** | Other codes in a different order than in the source | Info (that's what translation does) |
| **Missing annotation** | A source `<mrk>` isn't in the target | Info |

- **Per unit:** checks consider the unit's segments together, so a pair spanning segments is fine as long as both halves are somewhere in the target.
- **Only translated segments:** a segment without a target isn't checked.
- **Runs live:** the check re-runs for the edited unit after each edit (units are small).
- **Shown in the editor:** a badge on the segment row (count and worst severity), red outlines on the chips involved, and the issue list in the badge's tooltip.
- **Saving:** see [open question 2](#open-questions).

### 4. Keyboard workflow

Translators work from the keyboard. Proposed shortcuts, close to memoQ's, which many translators already know ([open question 6](#open-questions)):

| Shortcut | Action |
|---|---|
| `F9` | Insert the next missing code (wraps the selection for a pair) |
| `Ctrl+Shift+F9` | Pick a code to insert |
| `Ctrl+Insert` | Copy source to target |
| `Ctrl+Enter` | Confirm the segment (state Translated) and move to the next one |
| `Ctrl+Down` / `Ctrl+Up` | Next / previous segment |
| `Ctrl+Shift+Down` | Next segment with code issues |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Backspace`, `Delete`, arrows, `Shift`+arrows, `Home`/`End` | Treat a chip as one character |

### 5. Undo

- **Per-segment history:** each segment's target has its own undo history in the edit model, kept while the document is open, so leaving a segment and coming back still allows undoing.
- **Undo steps:** typing is grouped (a word or a pause ends a step). Inserting, deleting or moving a code is always its own step, as are copy source and paste.
- **What undo restores:** the target content and the caret or selection. It doesn't restore the segment state; reaching the original target again doesn't reset Translated to Initial ([open question 8](#open-questions)).
- **Out of scope:** undo across segments ("undo my last change wherever it was").

### 6. The target edit model

The heart of the design, in `SchrodingersCat.App` but free of Avalonia types:

```csharp
// The target as the editor sees it: text and code tokens, plus the source codes for reference.
public sealed class TargetEditModel
{
    public TargetEditModel(TranslationUnit unit, Segment segment);

    public IReadOnlyList<EditToken> Tokens { get; }        // TextToken("Hello "), CodeToken(InlineCode), MarkerToken(...)
    public int Caret { get; }                              // in token positions; a chip counts as 1
    public (int Start, int End) Selection { get; }

    public EditResult InsertText(string text);
    public EditResult InsertCode(string sourceCodeId);     // copies or reuses per canCopy; pairs insert both halves
    public EditResult InsertNextMissingCode();
    public EditResult Delete(Direction direction);         // refuses protected chips
    public EditResult CopySource();
    public EditResult Undo();
    public EditResult Redo();

    public InlineContent ToContent();                      // what gets written to Segment.Target
}

// Refused edits explain themselves, so the control can show a status message.
public sealed record EditResult(bool Applied, string? Refusal);
```

- **Conversion is exact:** `ToContent()` and loading from `InlineContent` round-trip exactly. The property-test generators from `SchrodingersCat.Core.Tests` (`XliffGen`) produce the content to prove it.
- **Edits reach the model directly:** every applied edit writes `Segment.Target`, as `TargetText` does today, so dirty tracking and saving stay unchanged.

## Editor approaches

All approaches sit on the same edit model. They differ in how much text-editing behaviour (caret, selection, IME, clipboard, accessibility) they get for free.

### A. Tokenised `TextBox`

The standard `TextBox` holds the target with each code half replaced by **one character from the Unicode Private Use Area** (U+E000 + index). A converter maps between that string and the edit model.

- **For:** native caret, selection, IME (Chinese/Japanese input), spell-check hooks, accessibility and undo. A PUA character is atomic, so the caret can't land inside a code. It's very little code.
- **Against:**
  - No chips: a `TextBox` can't style or replace individual characters, so codes show as tofu or as a glyph from a custom icon font (an extra asset, still no colours or state).
  - The `TextBox`'s own undo knows nothing about the rules, so refusing a protected deletion means reverting the text after the fact, which is flickery.
  - Copying to other apps leaks PUA characters unless the clipboard is intercepted.

### B. Custom chip control

A new control drawn with Avalonia's `TextLayout` and embedded runs for chips. It handles caret, selection, keyboard, mouse, IME (`TextInputMethodClient`), clipboard and accessibility itself.

- **For:** exactly the intended look and behaviour, no workarounds, no dependencies.
- **Against:** writing a text editor is a large, risky job. IME composition, bidirectional text (Arabic and Hebrew targets), screen readers, and mouse selection over wrapped lines are all hard to get right, and the rules aren't even the hard part.

### C. AvaloniaEdit with chip elements

[AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) (MIT; version 12.0.0 targets Avalonia 12) is the Avalonia port of AvalonEdit, a proven code-editor component. The document text holds one PUA character per code half (as in A). A `VisualLineElementGenerator` renders each one as an `InlineObjectElement` containing a chip control, and an `IReadOnlySectionProvider` plus an input handler routes deletions through the edit model.

- **For:**
  - Real chips: any Avalonia control, with colours, tooltips and click handling.
  - Atomic codes: the caret and selection treat the generated element as one unit.
  - Built-in, extensible undo stack, selection, clipboard hooks, and search.
  - Well-known extension points: element generators, read-only sections, input handlers.
  - The editor is only needed for the **active** segment: the other rows render read-only chips with a `SelectableTextBlock` and `InlineUIContainer`, which also keeps the list fast.
- **Against:**
  - A sizeable dependency.
  - It's a code editor at heart, so it needs styling to look like a text field.
  - IME and accessibility support in AvaloniaEdit has historically lagged behind `TextBox`, and must be checked on all three desktops.
  - One editor instance moving between rows complicates focus handling.

### Recommendation

**C, behind a spike.** It's the only option that delivers chips without writing a text editor. The edit model keeps the rules out of the control, so if the spike shows AvaloniaEdit can't do IME or accessibility acceptably, **A** works as a fallback with the same edit model and tests, at the cost of the chip look. B isn't worth the cost at this stage.

The spike (implementation step 3) checks, on Windows, Linux and macOS:

- typing with an IME (Japanese or Chinese), and accented input via dead keys;
- a screen reader announcing the text;
- right-to-left text;
- chips staying atomic under caret, selection, `Backspace` and `Delete`;
- headless testability.

## Spike (step 3)

`samples/EditorSpike` is a small Avalonia app, kept apart from `SchrodingersCat.App`, that edits one sample segment with AvaloniaEdit on top of `TargetEditModel`.
- **Encoding:** the document holds one private-use character (`U+E000 + n`) per code or annotation half, and a `VisualLineElementGenerator` draws it as a chip.
- **Every edit goes through the model:** a read-only section provider stops AvaloniaEdit from editing the document itself. Typing, Backspace/Delete, Ctrl+Z/Y, F9 and Ctrl+Insert are routed to the model, and its result is written back.

### Run it

```bash
dotnet run --project samples/EditorSpike            # Hungarian target
dotnet run --project samples/EditorSpike -- --rtl   # Hebrew target, for the right-to-left checks
```

The window shows the source, the chip editor, the model's status messages (refusals) and what the model holds. Paste is deliberately disabled in the spike; clipboard support is step 7.

### Verified automatically

`samples/EditorSpike.Tests` (headless, runs in CI) checks:
- **Chips:** every code and annotation half renders as a chip, and the document holds one private-use character per chip.
- **Atomic chips:** the arrow keys step over a chip in one move, and Shift+arrow selects a whole chip.
- **Deleting:** Backspace after a chip removes exactly that half through the model, leaving the other half. Delete on a protected chip is refused with the model's explanation.
- **Model routing:** typing and Ctrl+Z go through the model into the segment.
- **No direct edits:** AvaloniaEdit can't edit the document itself.
- **RTL sample:** it loads with the same chips.

### Manual checklist

These need a real desktop, because the headless platform has no input methods or accessibility tree. For each platform you have, run the spike, go through the checks, and note *pass*, *fail* (with what went wrong) or *n/a*.

| # | Check | How |
|---|---|---|
| 1 | **IME composition (Japanese)** | Add a Japanese input method: on Windows, Settings → Time & language → Language → add Japanese (switch with Win+Space); on macOS, System Settings → Keyboard → Input Sources → Japanese – Romaji; on Linux, fcitx5 or IBus with Mozc. Type `nihongo`, convert with Space, and confirm with Enter. *Pass:* 日本語 appears at the caret, the candidate window sits next to the caret, chips stay intact, and "What the model holds" shows the text. |
| 2 | **IME composition (Chinese)** | Same with a Chinese Pinyin input method: type `zhongwen` and pick 中文. *Pass:* as above. |
| 3 | **IME next to a chip** | Put the caret directly after a chip, then compose as in check 1. *Pass:* the composed text lands after the chip, and nothing is deleted or duplicated. |
| 4 | **Dead keys and accents** | Switch to a layout with dead keys (US International, or German), type `'` then `e`, and `` ` `` then `a`. *Pass:* é and à appear as single characters. |
| 5 | **AltGr and Option characters** | Type characters that need AltGr (Windows/Linux, e.g. `@` on a Hungarian layout) or Option (macOS, e.g. `€`). *Pass:* they're typed, not treated as shortcuts. |
| 6 | **Screen reader reads the text** | Start the screen reader: Narrator (Ctrl+Win+Enter) or NVDA on Windows, VoiceOver (Cmd+F5) on macOS, Orca on Linux. Focus the editor and move with the arrow keys. *Pass:* the text is read as you move, and a chip is announced somehow (even as an unknown character). |
| 7 | **Screen reader sees edits** | With the screen reader on, type a word and press Backspace. *Pass:* the changes are announced, or at least the text is re-read correctly. |
| 8 | **Right-to-left text** | Run with `--rtl`. Move through the Hebrew text with the arrow keys, select across a chip, and press Backspace next to a chip. *Pass:* the caret moves in visual order without jumping, the chips stay in place, and Backspace removes what you'd expect. |
| 9 | **Typing RTL** | Switch to a Hebrew or Arabic layout and type into the Hungarian sample, mixing it with Latin text. *Pass:* the text shapes and orders correctly around the chips. |
| 10 | **Mouse** | Click before and after a chip, double-click a word, and drag-select across chips. *Pass:* the caret never lands inside a chip, and the selection covers whole chips. |
| 11 | **Wrapping and resizing** | Make the window narrow so the line wraps at a chip. *Pass:* chips wrap like words and aren't clipped. |

Results:

| Check | Windows | Linux | macOS |
|---|---|---|---|
| 1–3 IME | | | |
| 4–5 Dead keys, AltGr/Option | | | |
| 6–7 Screen reader | | | |
| 8–9 Right-to-left | | | |
| 10–11 Mouse, wrapping | | | |

**Decision rule:**
- **Keep C (AvaloniaEdit) if:** IME (1–3) and the screen reader (6) work on the platforms that were tested, and nothing in 4–11 is broken in a way the editor control can't work around.
- **Switch to A (tokenised `TextBox`, same edit model) if:** IME or the screen reader fail.
- **Record the outcome:** the platforms tested and the outcome go into this section, as the decision note that closes step 3.

**Decision (2026-10-02):** keep C, AvaloniaEdit. The editor control (step 5) is built on AvaloniaEdit, using the spike's private-use encoding and its read-only section provider. Per-platform results weren't recorded in the table above. If a later manual run finds IME or screen-reader failures, the decision rule still applies and A remains the fallback, with the same `TargetEditModel`.

## Out of scope

- Creating brand-new codes (new native markup); see open question 9.
- Segment split and merge.
- Translation memory, machine translation and auto-propagation of codes.
- Undo across segments.
- Editing the source.

## Implementation steps

Each step ends with passing tests, zero warnings and green CI, and can be merged on its own.

1. **Core: `InlineCodeChecker`.** *(Done 2026-10-02.)* All issues from section 3, per unit, with segment and code references.
   - Unit tests for every issue type, including pairs spanning segments, copies with `dataRef` versus `copyOf`, and non-reorderable sequences.
   - A property test: a target that equals the source (or is a valid reordering) has no errors.
   - No UI changes.
2. **App: `TargetEditModel`.** *(Done 2026-10-02.)* Tokens, caret and selection, every operation from section 2 with the XLIFF rules, and undo/redo.
   - Unit tests per operation and per refusal.
   - A property test that `InlineContent` → model → `InlineContent` is exact.
   - Not yet wired to the UI.
3. **Spike: AvaloniaEdit.** *(Done 2026-10-02: keep AvaloniaEdit; see [Spike (step 3)](#spike-step-3).)* A throwaway branch or sample showing one target with chips: rendering, atomic caret, `Backspace` over a chip, and one headless test.
   - Manual checks for IME, screen reader and RTL on the three desktops.
   - Ends with a decision note in this document: C, or fall back to A.
4. **Read-only chips** for the source column and inactive targets (`SelectableTextBlock` and `InlineUIContainer`), replacing the `⟨1⟩` placeholder text in the list. Headless tests check the chips' labels, tooltips and states.
5. **Editor control** for the active segment's target, bound to `TargetEditModel`: typing, chips, `Backspace`/`Delete`, refusals shown as status messages, undo. Headless tests type around chips, try to delete a protected code, and undo.
6. **Integration.**
   - Remove the read-only rule for tagged segments.
   - Add code check badges and chip outlines.
   - Click a source chip to insert it.
   - A headless open → edit tagged segment → save test that re-reads the file and checks the codes.
7. **Keyboard workflow and clipboard:** the section 4 shortcuts, plus the clipboard format with a plain-text fallback. Headless tests per shortcut.
8. **Save policy and documentation:** implement the decision on open question 2, update CLAUDE.md, and mark this proposal implemented.

## Open questions

1. **Editor approach.** Accept C behind the spike, with A as the fallback? Or go straight to A for a quicker, plainer first version?
   **Decision (2026-10-02):** C, AvaloniaEdit, behind the spike in step 3, with A as the fallback if the spike fails. The spike kept C (see [Spike (step 3)](#spike-step-3)).
2. **Saving with code errors.**
   - Option (a): block saving until errors are fixed.
   - Option (b): warn ("3 segments have code errors. Save anyway?").
   - Option (c): save silently, with the badges as the only signal.

   *Proposal:* (b). The writer already refuses unpaired codes on its own, and translators sometimes need to save unfinished work.
   **Decision (2026-10-02):** (b), warn and let the translator save anyway.
3. **Deleting a protected code** (`canDelete="no"`).
   - Option (a): refuse the deletion (proposed).
   - Option (b): allow it, and flag it as an error.

   Option (a) is safer. Option (b) is less surprising when someone retypes a whole sentence.
   **Decision (2026-10-02):** (a), refuse the deletion and explain why.
4. **Deleting one half of a pair.** Delete only that half and let the check flag it (proposed), or delete both halves together?
   **Decision (2026-10-02):** delete only that half; the check flags the leftover until it is fixed or undone.
5. **Annotations in the target.**
   - Option (a): show and keep them like small codes.
   - Option (b): keep them only via copy source.
   - Option (c): drop them from the target entirely. They're metadata, and some tools regenerate terms anyway.

   *Proposal:* (a), with missing annotations only reported as info.
   **Decision (2026-10-02):** (a), show and keep them; a missing annotation is only reported as info.
6. **Shortcuts.**
   - memoQ-style `F9` (proposed).
   - Trados-style `Ctrl+,` for insert-tag.
   - Both, configurable later.

   **Decision (2026-10-02):** memoQ-style, as in section 4.
7. **Chip labels.** Short labels (`b`, `{0}`, id) with the native code in a tooltip, as proposed? Or a toggle between id, short and full native code, like the tag display modes some CAT tools offer?
   **Decision (2026-10-02):** short labels with the native code in the tooltip; a display-mode toggle can come later if needed.
8. **Segment state and undo.** Should undoing back to the original target return the state to what it was (for example, Initial)? *Proposal:* no. State changes only on edit and confirmation, never on undo.
   **Decision (2026-10-02):** no; undo restores content only.
9. **Brand-new codes.**
   - Option (a): leave them out (proposed).
   - Option (b): let translators add a small set of safe formatting codes (bold, italic) with predefined native data per file type. That would need per-format knowledge Core doesn't have.
   **Decision (2026-10-02):** (a), out of scope for now.
10. **Enforcing the spec's attribute constraint.** XLIFF requires `canCopy="no"` and `canDelete="no"` on codes with `canReorder="firstNo"/"no"`. Core's reader doesn't check this today. Should the reader reject files that break it, or should the editor treat such codes as protected regardless of what the file says (proposed: the latter, to stay lenient on input)?
   **Decision (2026-10-02):** keep the reader lenient; the edit model and checker treat codes in a non-reorderable sequence as non-deletable and non-copyable regardless of their attributes.
11. **Codes in ignorables.** Ignorables (usually whitespace between sentences) can contain codes. Should they be editable, or left exactly as extracted (proposed: as extracted)?
   **Decision (2026-10-02):** left exactly as extracted; ignorables aren't editable.
