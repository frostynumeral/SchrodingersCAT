# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

SchrödingersCAT is an early-stage CAT (computer-assisted translation) tool in C#/.NET. Right now it is only `SchrodingersCat.Core`: a UI-independent domain model plus an XLIFF 2.x serializer. A future Avalonia UI layer is planned (see the remarks in `Models/Segment.cs`).

## Commands

Solution file is `SchrodingersCat.slnx` (the new XML solution format, which needs SDK 9.0.200+).

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~Xliff20SerializerTests.Read_RejectsXliff12"   # single test
```

Projects target `net9.0`. The test project sets `<RollForward>Major</RollForward>` so tests also run on machines that only have a newer runtime, e.g. only .NET 10. Tests use xUnit, and `Xunit` is a global using.

## Architecture

- **`Models/`**: plain POCOs, organised as `TranslationDocument` → `TranslationFile` → `TranslationUnit` → `Segment`, with `Note`s attached at unit level. They mirror XLIFF 2.0 structure closely on purpose, so a round-trip loses nothing. Keep Core free of UI/MVVM dependencies; the UI layer will wrap these types in observable view models.
  - `Segment.IsIgnorable` models XLIFF `<ignorable>`. Ignorables sit in `TranslationUnit.Segments` in document order, because that order rebuilds the full `Source`/`Target` text. Use `TranslatableSegments` for anything the translator edits.
  - `Target == null` means "not translated yet". `""` means an intentionally empty translation. Keep the two distinct.
  - `SegmentState` covers only the four XLIFF states. Finer tool-specific states go in `SubState` (`prefix:value`, e.g. `cat:locked`) and are preserved verbatim.
- **`Xliff/`**: `IXliffSerializer` is stream-based. `XliffSerializerExtensions.WriteFileAsync` saves atomically: it writes to `<path>.tmp` and then moves it over the target. `Xliff20Serializer` handles XLIFF 2.0/2.1/2.2 core. Its design rules:
  - **Fail loudly instead of losing data.** Unsupported content, such as inline codes (`<ph>`, `<pc>`, `<mrk>`, …), XLIFF 1.2, invalid states and duplicate unit ids, throws `XliffFormatException`. Nothing is silently dropped, because the writer regenerates the whole file from the model and anything missing from the model would be lost on save.
  - Known limitations: `<group>` nesting is flattened, and modules/extensions are not preserved.
  - Whitespace is significant. Reading uses `LoadOptions.PreserveWhitespace`. Writing adds `xml:space="preserve"` when text has leading/trailing whitespace, runs of whitespace, or whitespace other than a plain space.
  - Output is always `version="2.0"`, UTF-8 without BOM, and indented.
