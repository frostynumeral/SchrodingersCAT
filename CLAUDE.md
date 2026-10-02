# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

SchrödingersCAT is an early-stage CAT (computer-assisted translation) tool in C#/.NET. Right now it is only `SchrodingersCat.Core`: a UI-independent domain model plus an XLIFF 2.x serializer. A future Avalonia UI layer is planned (see the remarks in `Models/Segment.cs`).

## Commands

Solution file is `SchrodingersCat.slnx` (the XML solution format).

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~Xliff20SerializerTests.Read_RejectsXliff12"   # single test
```

Projects target `net10.0`. CI (`.github/workflows/ci.yml`) builds and tests in Release on every push to `main` and on PRs. Tests use xUnit, and `Xunit` is a global using.

`Directory.Build.props` enables `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` for all projects, so any warning fails the build. Fix warnings rather than suppressing them; when a rule genuinely doesn't fit, disable it in `.editorconfig` (e.g. CA1707 is off under `tests/` for `Method_Scenario` test names).

## Architecture

- **`Models/`**: plain POCOs, organised as `TranslationDocument` → `TranslationFile` → `TranslationUnit` → `Segment`, with `Note`s attached at file, group or unit level. They mirror XLIFF 2.0 structure closely on purpose, so a round-trip loses nothing. Keep Core free of UI/MVVM dependencies; the UI layer will wrap these types in observable view models.
  - Files and groups hold `Items`: `TranslationUnit`s and nested `TranslationGroup`s (both derive from `TranslationItem`) in document order, so `<group>` nesting round-trips. `TranslationFile.Units`/`TranslationGroup.Units` are computed, recursive views; add units through `Items`.
  - Module and extension elements (matches, glossary, metadata, …) and `<skeleton>` are kept verbatim as detached `XElement`s in `ExtensionElements`/`Skeleton`. Foreign-namespace attributes on `<xliff>`, `<file>`, `<group>`, `<unit>` and `<note>` are kept as `ExtensionAttr`s in `ExtensionAttributes`. Core doesn't interpret either, so nothing updates them when content changes.
  - `Segment.IsIgnorable` models XLIFF `<ignorable>`. Ignorables sit in `TranslationUnit.Segments` in document order, because that order rebuilds the full `Source`/`Target` text. Use `TranslatableSegments` for anything the translator edits.
  - `Target == null` means "not translated yet". `InlineContent.Empty` means an intentionally empty translation. Keep the two distinct.
  - Segment content is `InlineContent`: an immutable, flat list of `InlineText`, `InlineCode` (`<ph>`/`<pc>`/`<sc>`/`<ec>`) and `InlineMarker` (`<mrk>`/`<sm>`/`<em>`). It is not a tree, and the two halves of a pair share an `Id`, so `<pc>` and `<sc>`/`<ec>` load into the same model and pairs can span segments. There is deliberately no implicit `string → InlineContent` conversion; use `InlineContent.FromText`. `<originalData>` lives in `TranslationUnit.OriginalData`, and foreign-namespace attributes are kept in `ExtensionAttributes`. Model records that hold lists use the internal `EquatableList<T>` so they keep value equality. The design and its rationale are in `docs/design/inline-codes.md`.
  - `SegmentState` covers only the four XLIFF states. Finer tool-specific states go in `SubState` (`prefix:value`, e.g. `cat:locked`) and are preserved verbatim.
- **`Xliff/`**: `IXliffSerializer` is stream-based. `XliffSerializerExtensions.WriteFileAsync` saves atomically: it writes to `<path>.tmp` and then moves it over the target. `Xliff20Serializer` handles XLIFF 2.0/2.1/2.2 core. Its design rules:
  - **Fail loudly instead of losing data.** Invalid or unsupported input, such as XLIFF 1.2, invalid states, duplicate ids, unmatched `<sc>`/`<ec>` pairs, unknown elements or attributes (inline or structural), foreign elements or attributes where XLIFF allows none, and stray text between elements, throws `XliffFormatException`. On writing, a model the reader would reject (duplicate inline, unit, group or file ids, unpaired halves, unknown `dataRef`s, invalid `srcDir`/`trgDir`) throws `InvalidOperationException`, and model values XLIFF has no place for (e.g. `Dir` on a standalone code, attributes on a closing marker, a state on an ignorable, an extension element in the XLIFF namespace) throw `NotSupportedException`. Nothing is silently dropped, because the writer regenerates the whole file from the model and anything missing from the model would be lost on save.
  - Known limitations: only things that carry no data are dropped: namespace prefixes (the writer reuses the original ones where it can, then the conventional module prefixes, then `ns1`, `ns2`, …), comments and processing instructions outside content, `xml:space` outside content, and `xml:lang` on `<source>`/`<target>`, which must repeat `srcLang`/`trgLang`.
  - Whitespace is significant. Reading uses `LoadOptions.PreserveWhitespace`. Writing adds `xml:space="preserve"` when text has leading/trailing whitespace, runs of whitespace, or whitespace other than a plain space.
  - Reading tracks which attributes have been read (`AttributeReader`, used for structural and inline elements alike), checks unit and group ids per file (`FileContext`), and keeps per-unit id and pairing state for sources and targets separately (`InlineSide`). `<cp>` is decoded on read and re-encoded for XML-invalid characters on write. Verbatim elements are copied with `Detach`, which also declares the prefixes they use so `DeclareExtensionNamespaces` can declare the same ones on the root.
  - The writer normalises well-formed pairs to `<pc>`/`<mrk>` (`FindWellFormedPairs`). Pairs that span segments, overlap, or whose halves `<pc>` can't express fall back to `<sc>`/`<ec>` and `<sm>`/`<em>`.
  - Output is always `version="2.0"`, UTF-8 without BOM, and indented. Content elements and verbatim elements start with an empty `XText` so XmlWriter's indentation can't inject whitespace into them.
- **Logging**: Core depends only on `Microsoft.Extensions.Logging.Abstractions`; the app picks the backend. Classes take an optional `ILogger<T>` in the constructor and fall back to `NullLogger<T>.Instance`, so they work without one. Use `[LoggerMessage]` source-generated methods (the class must be `partial`), because plain `LogError(...)` calls fail the build with CA1848/CA2254. Wrap arguments that cost something to compute in `_logger.IsEnabled(...)` and compute them in a local inside the guard; CA1873 still fires if the expression is passed inline. `Xliff20Serializer` logs at the public `ReadAsync`/`WriteAsync` boundary: Error on failure, attaching the exception and then re-throwing it, and Debug with a summary on success. Tests use `FakeLogger<T>` from `Microsoft.Extensions.Diagnostics.Testing`.

