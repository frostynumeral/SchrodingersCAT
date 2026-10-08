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

Open `samples/demo.en-he.xlf`.

| # | Do | Expect |
|---|---|---|
| 4.1 | Look at the target column of "Save" and "PressOk". | The Hebrew is right-aligned or at least shown in the right order. The chips in "PressOk" surround `אישור`. |
| 4.2 | Open "PressOk" in the editor. Press Home, then Right Arrow several times. | The caret moves visually and consistently, and passes a chip in one step. Write down whether Right moves visually right or logically forward. |
| 4.3 | Backspace and Delete next to the chips. | They remove the character on the expected side. A chip goes as a whole, or the delete is refused for a protected code. |
| 4.4 | Select with Shift+arrows across a chip and Ctrl+C, then paste into a new target. | The selection is highlighted contiguously, and the paste keeps the same text and chip order. |
| 4.5 | In "FilesCount" (empty target), type `העתק`, insert the code with F9, then type `קבצים`. | The code sits between the two Hebrew words, in that order in the target. |
| 4.6 | In "Mixed", type Hebrew with a Latin name inside (`קרא את מדריך SchrödingersCAT 2.0 קודם`). | Mixed-direction text is ordered correctly and the caret stays usable across the direction change. |
| 4.7 | Ctrl+Enter, Save as…, reopen the file. | The targets read back exactly as typed (the QA tab with F8 reports no code errors). |

## Recording the results

Fill in one row per desktop:

| Desktop (OS, version, desktop environment) | 1 IME | 2 Dead keys | 3 Screen reader | 4 RTL | Notes and findings |
|---|---|---|---|---|---|
| Windows | | | | | |
| Linux | | | | | |
| macOS | | | | | |

Then:
- Add the outcome to step 3 of [inline-code-editing.md](../design/inline-code-editing.md) as its decision note: C (AvaloniaEdit) stands, or what falls back to A.
- Remove "Not handled yet: the manual IME, screen reader and RTL checks" from CLAUDE.md, or replace it with what is still open.
- Open an issue for each finding with the check's number (e.g. "4.2: Right Arrow moves logically in RTL text").
