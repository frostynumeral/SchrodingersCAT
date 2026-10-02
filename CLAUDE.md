# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

SchrödingersCAT is an early-stage CAT (computer-assisted translation) tool in C#/.NET. It has two parts: `SchrodingersCat.Core`, a UI-independent domain model plus an XLIFF 2.x serializer, and `SchrodingersCat.App`, an Avalonia desktop editor built on it.

## Commands

Solution file is `SchrodingersCat.slnx` (the XML solution format).

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~Xliff20SerializerTests.Read_RejectsXliff12"   # single test
dotnet run --project src/SchrodingersCat.App                                         # start the editor
```

Projects target `net10.0`. CI (`.github/workflows/ci.yml`) builds and tests the whole solution in Release on every push to `main` and on PRs. Tests use xUnit, and `Xunit` is a global using. `SchrodingersCat.Core.Tests` is on xunit v2. `SchrodingersCat.App.Tests` is on xunit v3, because `Avalonia.Headless.XUnit` 12 requires it; xunit v3 test projects are executables (`OutputType` `Exe`), and its analyzers want `TestContext.Current.CancellationToken` passed to async APIs.

### Property-based tests

`Xliff20PropertyTests` uses [CsCheck](https://github.com/AnthonyLloyd/CsCheck) to generate random documents. Each property runs 100 iterations in a normal `dotnet test`. The properties:

- **Valid documents** (from `XliffGen` in `tests/SchrodingersCat.TestSupport`) round-trip losslessly: write→read gives back the same model, compared property by property via reflection in `ModelComparer`, so new model properties are covered automatically. Write→read→write gives the same XML.
- **Mutated models** (`ModelMutator`): the writer throws only `InvalidOperationException`/`NotSupportedException`, or writes a file that reads back and writes identically.
- **Mutated XML** (`XmlMutator`): structural, text and byte-level damage. The reader throws only `XliffFormatException`, or returns a model that writes, reads and writes identically.
- **Coverage:** `Generators_CoverWhatThePropertiesClaim` checks that a sample really contains the features the generators claim to produce (nested groups, spanning pairs, `<pc>` and `<sc>`/`<ec>`, `<cp>`, extensions, …), and that both mutation properties exercise both their accept and reject branches.

A failure prints the shrunk example (as the XLIFF it produces) and a seed. To reproduce it, and to dig deeper:

```bash
CsCheck_Seed=<seed> dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests.<test>"   # replay one failure
CsCheck_Iter=30000 dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests"           # more iterations
CsCheck_Time=120 dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests"             # run for N seconds
```

When a property finds a bug, fix it, and also add an example-based regression test, so the case stays covered even if the generators change. When the model gains a property, extend `XliffGen`/`DocumentBuilder` so valid documents use it. Generated documents must stay within what the reader can produce: for example, paired codes always have `CanOverlap` set, because the reader resolves it to the element's default.

`Directory.Build.props` enables `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` for all projects, so any warning fails the build. Fix warnings rather than suppressing them; when a rule genuinely doesn't fit, disable it in `.editorconfig` (e.g. CA1707 is off under `tests/` for `Method_Scenario` test names).

## Architecture

- **`Models/`**: plain POCOs, organised as `TranslationDocument` → `TranslationFile` → `TranslationUnit` → `Segment`, with `Note`s attached at file, group or unit level. They mirror XLIFF 2.0 structure closely on purpose, so a round-trip loses nothing. Keep Core free of UI/MVVM dependencies; the UI layer will wrap these types in observable view models.
  - Files and groups hold `Items`: `TranslationUnit`s and nested `TranslationGroup`s (both derive from `TranslationItem`) in document order, so `<group>` nesting round-trips. `TranslationFile.Units`/`TranslationGroup.Units` are computed, recursive views; add units through `Items`.
  - Module and extension elements (matches, glossary, metadata, …) and `<skeleton>` are kept verbatim as detached `XElement`s in `ExtensionElements`/`Skeleton`. Foreign-namespace attributes on `<xliff>`, `<file>`, `<group>`, `<unit>` and `<note>` are kept as `ExtensionAttr`s in `ExtensionAttributes`. Core doesn't interpret either, so nothing updates them when content changes.
  - `Segment.IsIgnorable` models XLIFF `<ignorable>`. Ignorables sit in `TranslationUnit.Segments` in document order, because that order rebuilds the full `Source`/`Target` text. Use `TranslatableSegments` for anything the translator edits.
  - `Target == null` means "not translated yet". `InlineContent.Empty` means an intentionally empty translation. Keep the two distinct.
  - Segment content is `InlineContent`: an immutable, flat list of `InlineText`, `InlineCode` (`<ph>`/`<pc>`/`<sc>`/`<ec>`) and `InlineMarker` (`<mrk>`/`<sm>`/`<em>`). It is not a tree, and the two halves of a pair share an `Id`, so `<pc>` and `<sc>`/`<ec>` load into the same model and pairs can span segments. There is deliberately no implicit `string → InlineContent` conversion; use `InlineContent.FromText`. `<originalData>` lives in `TranslationUnit.OriginalData`, and foreign-namespace attributes are kept in `ExtensionAttributes`. Model records that hold lists use the internal `EquatableList<T>` so they keep value equality. The design and its rationale are in `docs/design/inline-codes.md`.
  - `SegmentState` covers only the four XLIFF states. Finer tool-specific states go in `SubState` (`prefix:value`, e.g. `cat:locked`) and are preserved verbatim.
- **`Checks/`**: `InlineCodeHints` (built from a unit) says which source codes exist, which may not be deleted or copied, and which form non-reorderable sequences. The checker and the editor's `TargetEditModel` both use it, so they apply the same rules. `InlineCodeChecker.Check(unit)` compares a unit's target inline codes with its source codes and returns `InlineCodeIssue`s (kind, `IssueSeverity`, the segment it shows up in, the code id, and a message). It implements step 1 of `docs/design/inline-code-editing.md`:
  - **Errors:** a missing protected code; an unknown code; a copy of a non-copyable code; an unpaired half; a broken non-reorderable sequence.
  - **Warning:** a missing deletable code.
  - **Info:** reordered codes, including codes moved to another segment; a missing annotation.
  - **Scope:** it checks per unit, because pairs can span segments. Only translated, non-ignorable segments are compared with the source, but pairing covers the unit's whole target (ignorables included), as the writer does.
  - **Missing codes** are reported once per code. A half-present pair is reported as unpaired instead.
  - **Copies** are recognised by a reused `dataRef`, or by `copyOf` when the base code has no original data (XLIFF 4.7.2.4.1).
  - **Fixed-order sequences:** codes in a non-reorderable sequence of two or more codes count as non-deletable and non-copyable, whatever their attributes say (design decision 10). A lone `firstNo` code counts as reorderable.
  - It never throws on arbitrary targets, so the editor can run it on half-edited content; `InlineCodeCheckerPropertyTests` checks this.
- **`Xliff/`**: `IXliffSerializer` is stream-based. `XliffSerializerExtensions.WriteFileAsync` saves atomically: it writes to `<path>.tmp` and then moves it over the target. `Xliff20Serializer` handles XLIFF 2.0/2.1/2.2 core. Its design rules:
  - **Fail loudly instead of losing data.** Invalid or unsupported input, such as XLIFF 1.2, invalid states, duplicate ids, unmatched `<sc>`/`<ec>` pairs, unknown elements or attributes (inline or structural), foreign elements or attributes where XLIFF allows none, stray text between elements, a file with no units or groups (or a document with no files), targets without `trgLang`, duplicate note ids within one file, group or unit, and target `order` values that repeat within a unit or exceed its number of segments and ignorables, throws `XliffFormatException`. The writer refuses everything the reader would reject before it writes a byte, so it never produces a file that won't re-read and never fails inside `XmlWriter`. A model the reader would reject throws `InvalidOperationException`: duplicate inline, segment, unit, group or file ids; unpaired halves; unknown `dataRef`s; invalid `srcDir`/`trgDir`; a unit with no non-ignorable segment; a file with no items or a document with no files; a note priority outside 1–10; undefined enum values (`SegmentState`, `NoteAppliesTo`, `InlineCodeKind`, `InlineMarkerKind`, `CanReorder`); a skeleton with neither content nor `href`; targets without `TargetLanguage`; duplicate note ids within one parent; repeated or out-of-range `TargetOrder`s; and nulls in required properties or model lists. The cross-element constraints (`FindTargetOrderProblem`, `FindDuplicateNoteId`) are shared helpers, so reader and writer can't drift apart. Model values XLIFF has no place for throw `NotSupportedException`: e.g. `Dir` on a standalone code, attributes on a closing marker, a state on an ignorable, an extension element in the XLIFF namespace, a skeleton with XLIFF children or attributes other than `href`, an extension attribute name that isn't an XML name, XML-invalid characters anywhere `<cp>` can't be used (attribute values, note text, verbatim elements), and carriage returns in comments or processing instructions, which XML can't escape. `VerifyRepresentable` checks the last of these on the finished tree. Nothing is silently dropped, because the writer regenerates the whole file from the model and anything missing from the model would be lost on save.
  - **Input is untrusted.** XLIFF files come from clients and vendors, so the reader defends against hostile files, and each attack fails with `XliffFormatException` (logged like any other read failure):
    - DTDs are prohibited (`DtdProcessing.Prohibit`, `XmlResolver = null`). XLIFF never uses one, and refusing them rules out entity expansion ("billion laughs"), external entities (file or network reads) and external DTD subsets. The predefined entities (`&amp;` etc.) and character references still work.
    - Input is limited to `MaxInputBytes` (default 256 MiB, settable with `new Xliff20Serializer { MaxInputBytes = … }`) by `LimitedReadStream`, so an endless stream stops at the limit instead of exhausting memory.
    - Elements may nest at most `MaxElementDepth` (256) deep. `DepthLimitingXmlReader` enforces this while parsing: the reading code recurses, so a file nested a few thousand deep would otherwise overflow the stack and crash the process, and building a very deep XML tree is quadratic.
    - When changing how the reader loads XML, keep all three, and keep `Xliff20UntrustedInputTests` passing. The XML mutator in the property tests also produces DTD and entity input.
  - Known limitations: only things that carry no data are dropped: namespace prefixes (the writer reuses the original ones where it can, then the conventional module prefixes, then `ns1`, `ns2`, …), comments and processing instructions outside content, `xml:space` outside content, and `xml:lang` on `<source>`/`<target>`, which must repeat `srcLang`/`trgLang`.
  - Whitespace is significant. Reading uses `LoadOptions.PreserveWhitespace`. Writing adds `xml:space="preserve"` when text has leading/trailing whitespace, runs of whitespace, or whitespace other than a plain space.
  - Reading tracks which attributes have been read (`AttributeReader`, used for structural and inline elements alike), checks unit and group ids per file (`FileContext`), and keeps per-unit id and pairing state for sources and targets separately (`InlineSide`). `<cp>` is decoded on read and re-encoded for XML-invalid characters on write. Verbatim elements are copied with `Detach`, which also declares the prefixes they use so `DeclareExtensionNamespaces` can declare the same ones on the root.
  - The writer normalises well-formed pairs to `<pc>`/`<mrk>` (`FindWellFormedPairs`). Pairs that span segments, overlap, or whose halves `<pc>` can't express fall back to `<sc>`/`<ec>` and `<sm>`/`<em>`.
  - Output is always `version="2.0"`, UTF-8 without BOM, and indented. `NewLineHandling.Entitize` writes carriage returns as `&#xD;`; otherwise XML parsers would read them back as line feeds. Content elements and verbatim elements start with an empty `XText` so XmlWriter's indentation can't inject whitespace into them.
- **`SchrodingersCat.App`** (Avalonia 12, CommunityToolkit.Mvvm, compiled bindings): opens an XLIFF file, lists its translatable segments (ignorables are hidden), lets the translator edit targets, and saves through `WriteFileAsync` (atomic, so a failed save leaves the file intact): Save (Ctrl+S) writes back to the open file, Save as (Ctrl+Shift+S) writes to a new path that then becomes the open file.
  - `ViewModels/`: `MainWindowViewModel` (open/save commands, `FilePath`, `IsDirty`, `ErrorMessage`) and `SegmentViewModel`, which wraps a Core `Segment` and writes edits straight to it. View models use `[ObservableProperty]` partial properties and `[RelayCommand]`. Keep them free of Avalonia types so they stay unit-testable; dialogs (open, save as, the unsaved-changes prompt) go through `Services/IDialogService` (`AvaloniaDialogService` is the Avalonia implementation).
  - Errors: load and save failures are shown in the window's error banner instead of crashing. That covers `XliffFormatException`, `InvalidOperationException`/`NotSupportedException` from the writer, I/O and permission errors, and bad paths (`MainWindowViewModel.IsReportable`). Anything else is a bug and is deliberately not caught. A failed load keeps the current document open.
  - `Editing/TargetEditModel` (step 2 of `docs/design/inline-code-editing.md`, not wired to the UI yet) is one segment's target while it's edited.
    - **Tokens:** text, code halves and annotation halves.
    - **Positions:** the caret and selection count UTF-16 characters, a chip counts as one position, and a surrogate pair is never split.
    - **Operations:** each returns an `EditResult`, and a refused edit explains why.
      - `InsertText` replaces the selection.
      - `InsertCode` inserts missing halves with their source ids. A code that's already present gets a copy with a new unused id, reusing `dataRef`, or with `copyOf` when there's no `dataRef`. The copy is refused for non-copyable codes. A pair goes around the selection, or around the caret.
      - `InsertNextMissingCode` follows source order.
      - `Delete` never removes a protected code. It removes only one half of a pair, and deleting a selection keeps protected codes.
      - `CopySource`.
      - `Undo`/`Redo`: typing is grouped by word, and code edits are separate steps.
    - **Target writes:** every applied edit writes `Segment.Target` (an empty result means `null`, unless the target was intentionally empty). The model never changes `Segment.State`, not even on undo.
    - **Scope:** ignorables can't be edited.
  - Editing rules: segments whose source or target contains inline codes or markers are read-only (shown dimmed, codes rendered as `⟨1⟩…⟨/1⟩` by `InlineFormatter`), because editing them as plain text would drop the codes. Clearing a target sets it to `null` (not translated). The first translation moves a segment from `Initial` to `Translated`.
  - Unsaved changes are never dropped silently. Opening another file or closing the window while `IsDirty` asks Save / Don't save / Cancel (`UnsavedChangesDialog`). Cancel, or a save that fails, aborts the open or close. `UnsavedChangesChoice.Cancel` is deliberately the enum's default value, because a dialog closed without an answer (title bar, Escape) returns `default`. `MainWindow.OnClosing` cancels the close, awaits `ConfirmCloseAsync`, and closes again if allowed.
  - Not handled yet: tag editing (accepted design with ordered implementation steps in `docs/design/inline-code-editing.md`; follow its decisions), and documents that have targets but no `trgLang` (the save fails with the writer's message).
  - Tests (`tests/SchrodingersCat.App.Tests`): view-model tests with real files in a temp directory and a `FakeFileDialogs`, plus `[AvaloniaFact]`/`[AvaloniaTheory]` headless tests that drive the real `MainWindow` (typing, Ctrl+O/Ctrl+S/Ctrl+Shift+S, the error banner, the close prompt) and the real `UnsavedChangesDialog`. `FakeDialogs` answers dialogs with preset values and records what was asked. `TestAppBuilder` runs the real `App` on the headless platform, so no display is needed in CI. `TargetEditModelTests` and `TargetEditModelPropertyTests` cover every operation and refusal, the exact `InlineContent` round-trip, and random edit scripts that must never throw or lose a protected code.
- **`samples/EditorSpike`** (+ `EditorSpike.Tests`): the throwaway AvaloniaEdit spike from step 3 of the inline-code-editing design. AvaloniaEdit edits one segment through `TargetEditModel`: chips are private-use characters drawn by a `VisualLineElementGenerator`, and a read-only section provider blocks AvaloniaEdit's own edits so everything goes through the model. Run it with `dotnet run --project samples/EditorSpike` (add `-- --rtl` for a Hebrew target). The manual checklist and results live in the design doc. It isn't part of the app; the real editor control (step 5) will be built from what the spike shows.
- **`tests/SchrodingersCat.TestSupport`**: a plain library (not a test project) with the CsCheck document generators (`XliffGen`, `DocumentBuilder`), shared by both test projects. They can't reference each other, because they use different xunit versions.
- **Logging**: Core depends only on `Microsoft.Extensions.Logging.Abstractions`; the app picks the backend. Classes take an optional `ILogger<T>` in the constructor and fall back to `NullLogger<T>.Instance`, so they work without one. Use `[LoggerMessage]` source-generated methods (the class must be `partial`), because plain `LogError(...)` calls fail the build with CA1848/CA2254. Wrap arguments that cost something to compute in `_logger.IsEnabled(...)` and compute them in a local inside the guard; CA1873 still fires if the expression is passed inline. `Xliff20Serializer` logs at the public `ReadAsync`/`WriteAsync` boundary: Error on failure, attaching the exception and then re-throwing it, and Debug with a summary on success. Tests use `FakeLogger<T>` from `Microsoft.Extensions.Diagnostics.Testing`.

