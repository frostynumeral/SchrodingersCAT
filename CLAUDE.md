# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

SchrödingersCAT is an early-stage CAT (computer-assisted translation) tool in C#/.NET. It has three parts: `SchrodingersCat.Core`, a UI-independent domain model plus an XLIFF 2.x serializer; `SchrodingersCat.Memory`, the translation memory and the termbase (see `docs/design/translation-memory.md`, `docs/design/tm-maintenance.md` and `docs/design/termbase.md`); and `SchrodingersCat.App`, an Avalonia desktop editor built on both.

## Commands

Solution file is `SchrodingersCat.slnx` (the XML solution format).

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes                                                    # the formatting check CI runs
dotnet test --filter "FullyQualifiedName~Xliff20SerializerTests.Read_RejectsXliff12"   # single test
dotnet run --project src/SchrodingersCat.App                                         # start the editor
```

Projects target `net10.0`. `global.json` pins the SDK (10.0.1xx band, latest patch), locally and in both workflows, so `dotnet format` gives the same result everywhere; bump it deliberately, and run `dotnet format` after a bump because a newer formatter may want changes. Text files are LF (`.editorconfig`, `.gitattributes`). CI (`.github/workflows/ci.yml`) checks formatting with `dotnet format --verify-no-changes`, then builds and tests the whole solution in Release, on every push to `main` and on PRs; run `dotnet format` before pushing. Pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml`: it checks the tag against `VersionPrefix` in `Directory.Build.props` (bump that first), tests, publishes the app self-contained for `win-x64`, `linux-x64`, `osx-x64` and `osx-arm64` (without SkiaSharp's native `.pdb`s), and creates a GitHub release with the zips; 0.x and `-suffix` tags are pre-releases. Tests use xUnit, and `Xunit` is a global using. `SchrodingersCat.Core.Tests` is on xunit v2. `SchrodingersCat.App.Tests` is on xunit v3, because `Avalonia.Headless.XUnit` 12 requires it; xunit v3 test projects are executables (`OutputType` `Exe`), and its analyzers want `TestContext.Current.CancellationToken` passed to async APIs.

### Property-based tests

`Xliff20PropertyTests` uses [CsCheck](https://github.com/AnthonyLloyd/CsCheck) to generate random documents (generators in `tests/SchrodingersCat.TestSupport`). Each property runs 100 iterations in a normal `dotnet test`. What each property checks is in [docs/architecture.md](docs/architecture.md#property-based-tests).

A failure prints the shrunk example (as the XLIFF it produces) and a seed. To reproduce it, and to dig deeper:

```bash
CsCheck_Seed=<seed> dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests.<test>"   # replay one failure
CsCheck_Iter=30000 dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests"           # more iterations
CsCheck_Time=120 dotnet test --filter "FullyQualifiedName~Xliff20PropertyTests"             # run for N seconds
```

When a property finds a bug, fix it, and also add an example-based regression test, so the case stays covered even if the generators change. When the model gains a property, extend `XliffGen`/`DocumentBuilder` so valid documents use it. Generated documents must stay within what the reader can produce: for example, paired codes always have `CanOverlap` set, because the reader resolves it to the element's default.

`Directory.Build.props` enables `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` for all projects, so any warning fails the build. Fix warnings rather than suppressing them; when a rule genuinely doesn't fit, disable it in `.editorconfig` (e.g. CA1707 is off under `tests/` for `Method_Scenario` test names).

## Architecture

A map of the code. The details of each part are in [docs/architecture.md](docs/architecture.md); read the section for the part you work on, and follow the design doc it names. When you change a part, update its section in docs/architecture.md; add to CLAUDE.md only if a new part or a new cross-cutting rule appears.

- **Models** (Core, `Models/`): POCOs mirroring XLIFF 2.0 (`TranslationDocument` → `TranslationFile` → `TranslationUnit` → `Segment`, `TranslationGroup`s in `Items`, `Note`s, `InlineContent` of `InlineText`/`InlineCode`/`InlineMarker`). Design: `docs/design/inline-codes.md`. [Details](docs/architecture.md#models)
- **Checks** (Core, `Checks/`): `InlineCodeHints` and `InlineCodeChecker` (target codes against source codes). Design: `docs/design/inline-code-editing.md`. [Details](docs/architecture.md#checks)
- **Checks/Qa** (Core, `Checks/Qa/`, `Text/`): `QaChecker`, `IQaCheck`s, `QaText`, terminology (`TermRecord`, `ITermSource`, `GlossaryReader`), `DocumentChecker`, and the shared tokenizer/term rule in `Text/Words`. Design: `docs/design/qa-checks.md`, `docs/design/termbase.md`. [Details](docs/architecture.md#checksqa)
- **Xliff** (Core, `Xliff/`): `Xliff20Serializer` (XLIFF 2.0/2.1/2.2 core), `WriteFileAsync` (atomic save). [Details](docs/architecture.md#xliff)
- **App** (`SchrodingersCat.App`, Avalonia 12, CommunityToolkit.Mvvm): `MainWindowViewModel`, `SegmentViewModel`, `TargetEditModel`/`TargetEditor`, chips, matches, concordance, QA tab, TM menu and entry maintenance, the termbase (menu, terms list and source marks, Ctrl+T, Ctrl+E and the entry dialog, TBX/CSV import and export), settings. Designs: `inline-code-editing.md`, `qa-checks.md`, `translation-memory.md`, `tm-maintenance.md`, `termbase.md`. [Details](docs/architecture.md#app)
- **Memory** (`SchrodingersCat.Memory`): `ITranslationMemory` (`InMemoryTranslationMemory`, `SqliteTranslationMemory`), `MatchScore`, `Ranking`, `CodeTransfer`, `Tmx`, `Concordance`, `SourceDiff`, `XliffMatches`, and the termbase (`TermEntry`, `TermIndex`, `TermProbe`, `ITermbase`, `SqliteTermbase`, `Tbx`, `TermCsv`). Designs: `translation-memory.md`, `tm-maintenance.md`, `termbase.md`. [Details](docs/architecture.md#memory)
- **TestSupport** (`tests/SchrodingersCat.TestSupport`): the shared CsCheck generators (`XliffGen`, `DocumentBuilder`). [Details](docs/architecture.md#testsupport)
- **Logging**: `Microsoft.Extensions.Logging`; the app's rolling log file. [Details](docs/architecture.md#logging)

### Rules that apply everywhere

- Keep Core free of UI/MVVM dependencies. Keep view models free of Avalonia types so they stay unit-testable; dialogs go through `Services/IDialogService`.
- `Target == null` means "not translated yet"; `InlineContent.Empty` means an intentionally empty translation. Keep the two distinct. There is no implicit `string → InlineContent` conversion; use `InlineContent.FromText`.
- The serializer fails loudly instead of losing data: the reader throws `XliffFormatException` on anything invalid or unsupported, and the writer refuses (`InvalidOperationException`/`NotSupportedException`) everything the reader would reject, before writing a byte. Nothing is silently dropped.
- Input is untrusted. The XLIFF reader prohibits DTDs (`DtdProcessing.Prohibit`, `XmlResolver = null`), limits input to `MaxInputBytes` (`LimitedReadStream`) and nesting to `MaxElementDepth` (`DepthLimitingXmlReader`); when changing how it loads XML, keep all three and keep `Xliff20UntrustedInputTests` passing. TMX import, TM files (`*.sctm`) and termbase files (`*.sctb`) are untrusted too: they fail only with `TmFormatException`/`TermbaseFormatException`.
- `TermPattern.Key` and the index probes (`WordRun`/`IsKeyChar`) cut keys alike; never change one side alone.
- Logging: classes take an optional `ILogger<T>` and fall back to `NullLogger<T>.Instance`; log through `[LoggerMessage]` source-generated methods (the class must be `partial`), since plain `LogError(...)` fails the build (CA1848/CA2254). Never write `_ = SomethingAsync()`; use `Services/Background.Forget()`.
