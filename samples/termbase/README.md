# Termbase exchange samples

Inputs for the TBX and CSV import tests (`tests/SchrodingersCat.Memory.Tests`, `TbxTests` and `TermCsvTests`),
step 4 of `docs/design/termbase.md`. They are byte-exact: `.gitattributes` keeps git from normalising them (CRLF,
BOMs, UTF-16, Windows-1250), so edit them with care, or regenerate them.

**Origin and licence:** every file here was written by hand for SchrodingersCAT and is under the repository's MIT
licence. None comes from another tool: no tool's sample files had a licence that clearly allowed including them.

All of them are for an en → hu termbase.

| File | Format | What it exercises |
|---|---|---|
| `tbx2-basic.tbx` | TBX 2, UTF-8 | A DOCTYPE naming `TBXcoreStructV02.dtd` (skipped, never fetched); `tig` and `ntig`/`termGrp`; every status, by `administrativeStatus` (also in a `termNoteGrp`) and `normativeAuthorization`, both on one term, an unknown value, and none; notes at concept, language and term level; a definition in a `descripGrp`; origination and modification `transacGrp`s; a `de` language and `subjectField`, `partOfSpeech` and `admin` (dropped and counted); a regional variant (`hu-HU`); `<hi>` in a term; an entry without `en` and one with `en` and `de` only (both skipped); a source-only entry (kept). |
| `tbx2-utf16.tbx` | TBX 2, UTF-16 LE with BOM, CRLF | The encoding; `ő`, and a character outside the BMP. |
| `tbx2-latin1.tbx` | TBX 2, declared ISO-8859-1 | A legacy encoding the XML reader has built in. |
| `tbx3-basic-dca.tbx` | TBX 3, TBX-Basic, DCA | `conceptEntry`/`langSec`/`termSec`; a definition at concept and language level; a date with a fraction and an offset; `supersededTerm-admn-sts`; an `en-GB` variant; an `xref` (dropped). |
| `tbx3-basic-dct.tbx` | TBX 3, TBX-Basic, DCT | Module elements under unusual prefixes (`x` for Min, `y` for Basic), and a `min` prefix bound to a namespace that isn't TBX's, so matching is by namespace URI, not prefix; DCT transactions. |
| `utf8-bom-comma.csv` | CSV, UTF-8 with BOM, `,`, CRLF | Language names as headers (`English`, `Hungarian`); a quoted comma; doubled quotes; `|` between terms; statuses. |
| `utf8-semicolon.csv` | CSV, UTF-8 without BOM, `;`, LF | Tag headers and a `Comment` column; a line break inside quotes; a blank row; an extra cell. |
| `utf16-tab.txt` | Tab-separated, UTF-16 LE with BOM, CRLF | What Excel saves as "Unicode text"; regional tags (`en-US`, `hu-HU`) as headers. |
| `windows-1250.csv` | CSV, Windows-1250, `;`, CRLF | Not UTF-8, so the user has to choose the encoding; `ő` and `ű`; the native name `magyar` as a header. |
| `formulas-and-pipes.csv` | CSV, UTF-8 with BOM | The export's formula quote (`'=SUM(A1)`, `'@user`, `''+1`), a client's own `'til`, `folder \| directory`, an unknown status, and a row without a source term. |
