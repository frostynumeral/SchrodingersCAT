# Schrödinger's CAT tool

A computer-assisted translation (CAT) tool written in C#/.NET.

Website: <https://languageengineer.hu/schrodingerscat/>

> **Status:** early development. There is a core library (a translation document model, an XLIFF 2.x reader/writer and QA checks), a translation memory and termbase library, and a desktop editor (Avalonia).

## What works so far

### XLIFF

- Reads and writes **XLIFF 2.0 / 2.1 / 2.2** (core).
- Maps files, nested `<group>`s, units, segments, ignorables, notes, and segment states (`initial` / `translated` / `reviewed` / `final`), including tool-specific `subState` values.
- Preserves significant whitespace on round-trip.
- Inline codes and annotations (`<ph>`, `<pc>`, `<sc>`/`<ec>`, `<mrk>`, `<sm>`/`<em>`), `<cp>` and `<originalData>` round-trip. Well-formed pairs are saved as `<pc>`/`<mrk>`, which XLIFF treats as equivalent to `<sc>`/`<ec>` and `<sm>`/`<em>`.
- Module and extension elements (matches, glossary, metadata, …), `<skeleton>`, and foreign attributes are kept verbatim.
- Saves atomically: a failed save never leaves a half-written file.
- **Never silently loses data.** Content the model can't represent is rejected with a clear error instead of being dropped on save.
- **Treats input as untrusted:** DTDs and entities are refused, and input size and nesting depth are limited.

### Editor

- Opens an XLIFF file (`Ctrl+O`) and lists its segments with source and target side by side.
- Inline codes are shown as chips. Targets with codes are edited with the codes kept as chips: protected codes can't be deleted, and copies follow XLIFF's editing hints.
- A code check runs as you type: each segment shows a badge for missing, unknown, unpaired or reordered codes, and the chips involved are outlined. Saving with code errors asks first.
- Keyboard workflow (close to memoQ's): `F9` inserts the next missing code, `Ctrl+Shift+F9` picks one, `Ctrl+Insert` copies the source, `Ctrl+Enter` confirms and moves on, `Ctrl+Up`/`Ctrl+Down` move between segments, `Ctrl+Shift+Down` goes to the next segment with code or QA problems. Copy and paste keep codes inside the app.
- Translation memory: create or open a TM (`*.sctm`, SQLite) from the Translation memory menu, import and export TMX; the open TM is remembered between sessions. `Ctrl+Enter` stores the confirmed segment, so repetitions further down get it straight away.
- Concordance: `Ctrl+K` searches the TM for the selected word or phrase (or opens the search box), in sources or translations, matching word beginnings so suffixed forms are found too; Insert puts a translation at the caret.
- A matches pane under the segment list shows the selected segment's matches from the TM and from the XLIFF file, with what differs from the current source marked; `Ctrl+1` … `Ctrl+9` insert one, with the current segment's codes. Exact TM matches go into empty targets automatically, unconfirmed.
- Matches and concordance hits from the TM can be edited (the translation) or deleted, with their buttons or context menu; `Ctrl+Shift+1` … `Ctrl+Shift+9` delete a match.
- Per-segment undo, Save (`Ctrl+S`) and Save as (`Ctrl+Shift+S`), and a prompt before unsaved changes would be lost. A file without a target language asks for it on the first save.
- The app writes a rotating log file, crashes included; Help → Open log folder finds it for a bug report.

### QA checks

- Quality checks run as you type, next to the code check, and show in the segment's badge: numbers, target same as source, `translate="no"` text changed, repeated words, terminology (warnings); spaces, end punctuation, unpaired brackets and quotes (info).
- Terminology is checked against the file's own glossary and the open termbase: a term in the source needs one of its allowed translations in the target (a forbidden one doesn't count).
- `F8` opens the QA tab with a report for the whole document, grouped by check, adding untranslated segments and inconsistent translations or sources. Clicking an entry or pressing Enter goes to the segment; fixed issues are marked.
- Quality issues can be dismissed per segment (from the badge or the report); a change to the target brings them back. Settings… switches individual checks off, and the choice is remembered.

### TM entry maintenance

- Translation memory → Manage entries… opens a window listing the TM's entries, newest first, filtered by words in the sources or translations, document, user, date range, or sources with several translations.
- Edit the translation of an entry (in the same editor, with codes as chips) or delete it; Delete all matching… removes every entry the filter shows. Deleting always asks first.

### Termbase

- Create or open a termbase (`*.sctb`, SQLite) from the Termbase menu; the open termbase is remembered between sessions. It serves documents in its language pair (regional variants included).
- Terms found in the source are underlined, with their translations in the tooltip, and listed under the matches with an Insert button. Matching uses word beginnings, so suffixed forms are found too (no stemming).
- `Ctrl+T` picks a term translation to insert at the caret.
- `Ctrl+E` (Termbase → Add term…) adds a term, prefilled from the selection in the source and the target; it can start a new entry or add to an existing one. Entries are edited or deleted from the terms list's context menu.
- Import TBX (2 and 3) and CSV/TSV (with a dialog for the encoding and columns); export TBX 2, TBX 3 or CSV.

### Not supported yet

- XLIFF 1.2.
- Search and replace in the document or the TM, and filtering the segment list.
- A review workflow: the editor only moves segments from `initial` to `translated`; it doesn't set `reviewed` or `final`.
- Several termbases or TMs at once, and shared (server) TMs and termbases.
- Spelling and grammar checks, and exporting the QA report.

## Download

Pre-release builds for Windows, Linux and macOS (Intel and Apple silicon) are on the [Releases](../../releases) page. They include .NET, so nothing else needs installing: unzip and run `SchrodingersCat.App`. The builds aren't signed, so Windows SmartScreen and macOS Gatekeeper will warn the first time. On macOS, run `xattr -dr com.apple.quarantine SchrodingersCat` on the unzipped folder first.

## Building

Requires the .NET 10 SDK (`global.json` pins the version band).

```bash
dotnet build
dotnet test
dotnet run --project src/SchrodingersCat.App   # start the editor
```

`samples/demo.en-hu.xlf` is a small file to try the editor with.

The build runs the .NET recommended code analyzers and treats every warning as an error, so a build with warnings fails. CI checks formatting (`dotnet format --verify-no-changes`), then builds and tests every push to `main` and every pull request; run `dotnet format` before pushing.

## Project layout

```
src/SchrodingersCat.Core/           Domain model (Models/), inline code and QA checks (Checks/), XLIFF serializer (Xliff/)
src/SchrodingersCat.Memory/         Translation memory (SQLite, TMX, concordance) and termbase (SQLite, TBX, CSV)
src/SchrodingersCat.App/            Avalonia desktop editor
tests/SchrodingersCat.Core.Tests/   Core tests (xUnit v2), including property-based tests (CsCheck)
tests/SchrodingersCat.Memory.Tests/ Translation memory and termbase tests (xUnit v2)
tests/SchrodingersCat.App.Tests/    View model and headless UI tests (xUnit v3)
tests/SchrodingersCat.TestSupport/  Random document generators shared by the test projects
docs/architecture.md                A map of the code
docs/design/                        Design notes (inline codes, inline code editing, QA checks, translation memory, TM maintenance, termbase)
docs/testing/                       Manual test checklists
samples/                            Demo XLIFF files, and sample TBX/CSV files (termbase/)
tools/                              Python tools (stdlib only), e.g. a reader for the app's log: python3 -m tools.util.logging
Directory.Build.props               Shared build settings (analyzers, warnings as errors)
.editorconfig                       Code style and analyzer rule overrides
```

## Usage

```csharp
using SchrodingersCat.Core.Xliff;

var serializer = new Xliff20Serializer();
var document = await serializer.ReadFileAsync("job.xlf");

foreach (var segment in document.TranslatableSegments)
    Console.WriteLine($"{segment.State}: {segment.Source.ToPlainText()} → {segment.Target?.ToPlainText()}");

await serializer.WriteFileAsync(document, "job.xlf");
```

## License

[MIT](LICENSE)
