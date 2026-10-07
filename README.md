# Schrödinger's CAT tool

A computer-assisted translation (CAT) tool written in C#/.NET.

> **Status:** early development. There is a core library (a translation document model and an XLIFF 2.x reader/writer) and a first version of the desktop editor (Avalonia).

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

- Opens an XLIFF file and lists its segments with source and target side by side.
- Inline codes are shown as chips. Targets with codes are edited with the codes kept as chips: protected codes can't be deleted, and copies follow XLIFF's editing hints.
- A code check runs as you type: each segment shows a badge for missing, unknown, unpaired or reordered codes, and the chips involved are outlined. Saving with code errors asks first.
- Keyboard workflow (close to memoQ's): `F9` inserts the next missing code, `Ctrl+Shift+F9` picks one, `Ctrl+Insert` copies the source, `Ctrl+Enter` confirms and moves on, `Ctrl+Up`/`Ctrl+Down` move between segments, `Ctrl+Shift+Down` goes to the next segment with code problems. Copy and paste keep codes inside the app.
- Translation memory: create or open a TM (`*.sctm`, SQLite) from the Translation memory menu, import and export TMX; the open TM is remembered between sessions. `Ctrl+Enter` stores the confirmed segment, so repetitions further down get it straight away.
- A matches pane under the segment list shows the selected segment's matches from the TM and from the XLIFF file, with what differs from the current source marked; `Ctrl+1` … `Ctrl+9` insert one, with the current segment's codes. Exact TM matches go into empty targets automatically, unconfirmed.
- Per-segment undo, Save (`Ctrl+S`) and Save as (`Ctrl+Shift+S`), and a prompt before unsaved changes would be lost. A file without a target language asks for it on the first save.

### Not supported yet

- XLIFF 1.2.
- Concordance search and editing TM entries.
- Terminology, search, and review workflows.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/SchrodingersCat.App   # start the editor
```

`samples/demo.en-hu.xlf` is a small file to try the editor with.

The build runs the .NET recommended code analyzers and treats every warning as an error, so a build with warnings fails. CI builds and tests every push to `main` and every pull request.

## Project layout

```
src/SchrodingersCat.Core/           Domain model (Models/), inline code checks (Checks/) and XLIFF serializer (Xliff/)
src/SchrodingersCat.Memory/         Translation memory (in progress)
src/SchrodingersCat.App/            Avalonia desktop editor
tests/SchrodingersCat.Core.Tests/   xUnit tests, including property-based tests (CsCheck)
tests/SchrodingersCat.Memory.Tests/ Translation memory tests
tests/SchrodingersCat.App.Tests/    View model and headless UI tests
tests/SchrodingersCat.TestSupport/  Random document generators shared by the test projects
docs/design/                        Design notes (inline codes, inline code editing, translation memory)
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
