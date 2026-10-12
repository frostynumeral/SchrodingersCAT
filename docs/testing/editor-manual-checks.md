# Manual checks: IME, screen reader and right-to-left text in the editor

Step 3 of [inline-code-editing.md](../design/inline-code-editing.md) chose AvaloniaEdit for the target editor, on
condition that IME input, screen readers and right-to-left text work acceptably on all three desktops. Headless tests
can't check any of these, so they need a person at a real desktop: one run on Windows, one on Linux and one on macOS.
This is the checklist for that run.

## Before you start

- Start the app from the repository: `dotnet run --project src/SchrodingersCat.App`, or a release build.
- Files:
  - `samples/demo.en-hu.xlf`: Hungarian (left to right), with codes. Use it for the IME, dead-key and screen-reader
    checks.
  - `samples/demo.en-he.xlf`: Hebrew (right to left), with paired codes and a placeholder.
- Don't save over the samples (Save as… to a temporary file), or restore them afterwards with `git checkout samples`.
- If the app crashes, Help → Open log folder shows the log; attach `schrodingerscat.log` to the result.
- In the checks, *the editor* is the active row's target (click a target, or Ctrl+Down to a segment). A *chip* is a
  code drawn as a small box (`⟨b`, `b⟩`, `{0}`).
- Input methods and layouts are added in the system settings:
  - Windows: Settings → Time & language → Language & region → add Japanese, Chinese, Hebrew or Arabic. Win+Space
    switches between them.
  - macOS: System Settings → Keyboard → Input Sources.
  - Linux: fcitx5 or IBus (Mozc for Japanese, Pinyin for Chinese), and the desktop's keyboard settings for layouts.
- Screen readers start with:
  - Narrator: Ctrl+Win+Enter.
  - NVDA: Ctrl+Alt+N.
  - VoiceOver: Cmd+F5.
  - Orca: Super+Alt+S on GNOME.

## 1. IME (Japanese or Chinese input)

Set up an input method:
- Windows: Microsoft IME (Japanese).
- Linux: Mozc or Pinyin under fcitx5 or IBus.
- macOS: Japanese – Romaji or Pinyin – Simplified.

In an empty target (Ctrl+Down until a segment has none, or clear one), and between two chips:

| # | Do | Expect |
|---|---|---|
| 1.1 | Type `nihongo` (or `zhongwen`) without committing. | The composition shows inline at the caret, underlined, and doesn't change the target yet. |
| 1.2 | Look at the candidate window. | It opens next to the caret, not at a corner of the window or screen. |
| 1.3 | Commit (Enter or Space, then Enter). | The chosen text is inserted at the caret, once. The status line shows no refusal. |
| 1.4 | Compose with the caret right after a chip, then commit. | The text goes after the chip. The chip is still there and still atomic. |
| 1.5 | Select across a chip, then compose and commit. | It acts like typing over a selection: protected codes stay, the text replaces the rest. |
| 1.6 | Press Escape during a composition. | The composition is cancelled and the target is unchanged. |
| 1.7 | Ctrl+Z after a commit. | The committed text is undone in one step. |
| 1.8 | Ctrl+Enter, then reopen the segment. | The committed text is saved in the target. |

## 2. Dead keys and accented input

Use a layout with dead keys:
- Windows: US International.
- Linux: US intl. with dead keys.
- macOS: the Option dead keys (Option+E, E → é), and press-and-hold for accents.

| # | Do | Expect |
|---|---|---|
| 2.1 | `'` then `e`. | `é` is inserted once, with no stray `'`. |
| 2.2 | `'` then a space. | A plain `'`. |
| 2.3 | macOS press-and-hold `e`, then choose `è`. | `è` replaces the `e`. |
| 2.4 | Hungarian layout: `ő`, `ű`. | Both are inserted directly. |
| 2.5 | A dead key right before a chip, then a letter. | The accented letter goes before the chip. |
| 2.6 | Windows and Linux: characters that need AltGr, e.g. `@` (AltGr+V) and `{` (AltGr+B) on a Hungarian layout. | They are typed, not taken as shortcuts (AltGr is Ctrl+Alt on Windows). |
| 2.7 | macOS: characters that need Option, e.g. `€` (Option+Shift+2 on a US layout). | They are typed, not taken as shortcuts. |

## 3. Screen reader

Screen readers to use:
- Windows: Narrator and NVDA.
- Linux: Orca.
- macOS: VoiceOver.

| # | Do | Expect |
|---|---|---|
| 3.1 | Move to a segment with Ctrl+Down. | The editor's text is read, or at least announced as an editable text field with its content. |
| 3.2 | Move the caret with the arrow keys and Ctrl+arrow. | Each character or word is read. |
| 3.3 | Move over a chip. | Write down what is read: the chip's label, "object" or "replacement character", or nothing. Silence or a raw code point is a finding. |
| 3.4 | Type a word. | The typed characters are echoed. |
| 3.5 | Press Delete on a protected chip. | Write down whether the refusal in the status line is announced. It probably isn't; that's a finding, not a failure of this check. |
| 3.6 | Tab to the source column and the matches pane. | Write down whether they are reachable and read. |

## 4. Right-to-left text

Open `samples/demo.en-he.xlf`. Typing Hebrew (4.5, 4.6) needs a Hebrew keyboard layout, and 4.8 an Arabic one.

| # | Do | Expect |
|---|---|---|
| 4.1 | Look at the target column of "Save" and "PressOk". | The Hebrew is right-aligned or at least shown in the right order. The chips in "PressOk" surround `אישור`. |
| 4.2 | Open "PressOk" in the editor. Press Home, then Right Arrow several times. | The caret moves visually and consistently, and passes a chip in one step. Write down whether Right moves visually right or logically forward. |
| 4.3 | Backspace and Delete next to the chips. | They remove the character on the expected side. A chip goes as a whole, or the delete is refused for a protected code. |
| 4.4 | Select with Shift+arrows across a chip and Ctrl+C, then paste into a new target. | The selection is highlighted contiguously, and the paste keeps the same text and chip order. |
| 4.5 | In "FilesCount" (empty target), type `העתק`, insert the code with F9, then type `קבצים`. | The code sits between the two Hebrew words, in that order in the target. |
| 4.6 | In "Mixed", type Hebrew with a Latin name inside (`קרא את מדריך SchrödingersCAT 2.0 קודם`). | Mixed-direction text is ordered correctly and the caret stays usable across the direction change. |
| 4.7 | Ctrl+Enter, Save as…, reopen the file. | The targets read back exactly as typed (the QA tab with F8 reports no code errors). |
| 4.8 | With an Arabic layout, type `مرحبا` into an empty target, then insert a code between two words. | The letters join (shaped forms, not isolated letters), also next to the chip, and the word order stays right. |

## 5. Mouse and wrapping

Any sample file.

| # | Do | Expect |
|---|---|---|
| 5.1 | Click just before and just after a chip in the editor. | The caret lands before or after the chip, never inside it. |
| 5.2 | Double-click a word next to a chip. | The word is selected, without the chip. |
| 5.3 | Drag-select across one or more chips. | The selection covers whole chips, never half of one. |
| 5.4 | Make the window narrow, so that a target's line wraps at a chip. | The chip wraps like a word, isn't clipped, and the caret moves across the wrap normally. |

## Recording the results

Fill in one row per desktop:

| Desktop (OS, version, desktop environment) | 1 IME | 2 Dead keys, AltGr/Option | 3 Screen reader | 4 RTL | 5 Mouse, wrapping | Notes and findings |
|---|---|---|---|---|---|---|
| Windows | | | | | | |
| Linux | | | | | | |
| macOS | | | | | | |

The decision rule, from step 3 of the design:
- **AvaloniaEdit stays (option C)** if IME (1) and the screen reader (3.1–3.4) work on the desktops tested, and nothing
  in 2, 4 or 5 is broken in a way the editor control can't work around.
- **Fall back to a tokenised `TextBox` (option A)**, with the same `TargetEditModel` and its tests, if IME or the
  screen reader fail.

Then:
- Add the outcome to step 3 of [inline-code-editing.md](../design/inline-code-editing.md): the desktops tested, and whether C stands.
- Remove "Not handled yet: the manual IME, screen reader and RTL checks" from docs/architecture.md, or replace it with what is still open.
- Open an issue for each finding with the check's number (e.g. "4.2: Right Arrow moves logically in RTL text").

## The termbase (step 5 of docs/design/termbase.md)

Open a document with an en→hu termbase (and `samples/demo.en-he.xlf` with an en→he one for right-to-left text).

- **IME:** composing CJK text in the term picker's filter (`TermFilter`) filters correctly, and Enter during composition commits the text instead of choosing a term.
- **Screen readers** (Narrator, VoiceOver, Orca):
  - term rows read the source, the translations and "forbidden: …" / "preferred: …"
  - the non-focusable Insert button is announced, or the terms list is enough without it
  - the picker announces the selected choice
  - the source underline is announced, or the list is enough without it
- **Tooltip with the real renderer:** hovering a term in the source shows its tooltip. The headless test can't prove it lands on the text, because headless drawing records no glyphs.
- **RTL** (`samples/demo.en-he.xlf` with an en→he termbase): underlines and tooltips land on the right Hebrew words across bidi runs; translations in the list and picker are aligned and ordered correctly; an inserted term lands at the logical caret.
- **Ctrl+T on macOS:** whether it should be Cmd+T instead, and that localised keyboard layouts don't type a character instead.

## Adding and editing terms, import and export (step 6 of docs/design/termbase.md)

Open a document with an en→hu termbase (and `samples/demo.en-he.xlf` with an en→he one).

- **Ctrl+E on macOS and other layouts:** whether it should be Cmd+E; that layouts where Ctrl+E types a character (or AltGr layouts on Windows) still open the dialog; that Termbase → Add term… shows the shortcut as the desktop writes it.
- **Selections:** with text selected in the active row's source and in its editor, Ctrl+E fills both in (codes as spaces); with a selection left in another row's source, the active row's wins.
- **IME:** composing CJK text in the dialog's term and note boxes, and Ctrl+Enter during composition (it should commit the text, not save half of it).
- **Screen readers** (Narrator, VoiceOver, Orca): each row's term box, status list, note box and × button are announced with their names; the list of existing entries ("Make a new entry", "Add to “file” → …") reads its choices and which is selected; the problem line is announced when a save is refused.
- **RTL** (en→he termbase): Hebrew terms typed in the dialog's target rows display and edit correctly, and come back in the right order in the terms list and the marks.
- **Context menu:** right-click (and the menu key, and Shift+F10) on a term row opens Edit entry… / Delete entry…; the confirmation's Escape cancels.
- **CSV import dialog** with a real Excel file in a legacy encoding (e.g. a Hungarian Excel "CSV (comma delimited)" in Windows-1250): the dialog asks for the encoding, the preview shows the accented letters right once it's chosen, and the guessed columns are right.
- **Export picker:** on each desktop (GTK or the portal on Linux, macOS, Windows), the file-type choice (TBX 2, TBX 3, CSV) decides the format written, and a desktop that doesn't report it falls back to the extension (`.csv` is CSV, anything else TBX 2).
- **Export into another CAT tool:** still step 4's pending hand check (docs/design/termbase.md): import the TBX 2, TBX 3 and CSV exports into at least one other tool.
