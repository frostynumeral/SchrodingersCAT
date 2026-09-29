# SchrödingersCAT

A computer-assisted translation (CAT) tool written in C#/.NET.

> **Status:** early development. So far there is only the core library: a translation document model and an XLIFF 2.x reader/writer. A desktop UI (Avalonia) is planned.

## What works so far

- Reads and writes **XLIFF 2.0 / 2.1 / 2.2** (core).
- Maps files, units, segments, ignorables, notes, and segment states (`initial` / `translated` / `reviewed` / `final`), including tool-specific `subState` values.
- Preserves significant whitespace on round-trip.
- Saves atomically: a failed save never leaves a half-written file.
- **Never silently loses data.** Content the model can't represent yet is rejected with a clear error instead of being dropped on save.

### Not supported yet

- Inline codes (`<ph>`, `<pc>`, `<mrk>`, …): files that contain them are rejected.
- XLIFF 1.2.
- `<group>` hierarchy: groups are flattened.
- XLIFF modules/extensions (matches, glossary, …): these are not preserved on save.

## Building

Requires the .NET SDK 9.0.200 or newer. The solution uses the `.slnx` format.

```bash
dotnet build
dotnet test
```

## Project layout

```
src/SchrodingersCat.Core/         Domain model (Models/) and XLIFF serializer (Xliff/)
tests/SchrodingersCat.Core.Tests/ xUnit tests
```

## Usage

```csharp
using SchrodingersCat.Core.Xliff;

var serializer = new Xliff20Serializer();
var document = await serializer.ReadFileAsync("job.xlf");

foreach (var segment in document.TranslatableSegments)
    Console.WriteLine($"{segment.State}: {segment.Source} → {segment.Target}");

await serializer.WriteFileAsync(document, "job.xlf");
```
