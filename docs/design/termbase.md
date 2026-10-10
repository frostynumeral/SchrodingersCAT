# Proposal: termbase

**Status:** accepted (2026-10-10). All [open questions](#open-questions) are decided as proposed; implementation
follows the [steps](#implementation-steps), starting with step 1.

## Problem

Translators have to use the client's terms: the product names, the UI strings, the one translation of "folder" the
client chose years ago. A termbase is where those terms live, across documents and projects. A CAT tool looks the
active segment's source up in it, shows the terms it finds with their translations, inserts one with a key, and
warns when a translation doesn't use them.

The editor has only half of this. The QA terminology check ([qa-checks.md](qa-checks.md), step 2) reads the
glossary module a file may carry (`<gls:glossary>`), and only for that file. Its open question 3 left "a translator's
termbase (a new store, import from TBX/CSV)" for a proposal of its own. This is that proposal. Today:

- terms a file doesn't carry can't be checked or looked up at all;
- terms aren't shown while translating, only reported afterwards by the check;
- the translator can't record a term they decided on, so the next document starts from nothing.

## What a termbase has to do

- **Store** term entries for one language pair: a source term, its translations, optionally synonyms, notes, and
  whether a term is preferred or forbidden.
- **Find** the entries whose source term is in the active segment's source, by the same rules as the QA check, so
  what the pane shows, what the source column marks and what the check warns about always agree.
- **Show** the terms found for the active segment, and mark them in the source column.
- **Insert** a term's translation at the caret with a key.
- **Learn:** add a term from the selected source and target text without leaving the editor.
- **Check:** the QA terminology check uses the termbase as well as the file's glossary.
- **Exchange:** import and export TBX, the standard format every CAT tool reads and writes, and CSV, the format
  clients most often send terms in.

## Proposed design

Layers, as for the translation memory, each testable on its own:

1. **QA and matching rules** (`SchrodingersCat.Core`): the terminology check takes term entries from outside the
   file, and the term rule handles terms with punctuation.
2. **Entries and finding them** (`SchrodingersCat.Memory`, no storage, no UI): entries, and an in-memory index that
   finds them in a text.
3. **Storage** (same project): a termbase file on disk, loaded into that index.
4. **Exchange** (same project): TBX and CSV import and export.
5. **Integration** (`SchrodingersCat.App`): the terms list, marking terms in the source, inserting and adding terms,
   and choosing the termbase.

Memory already has the SQLite package and the defences for untrusted files, and it already sees Core's internal
`Words`, whose `FindTerm` the QA check uses. A new project would need both again ([open question
1](#open-questions)).

### 1. Entries

```csharp
public sealed record TermEntry(
    IReadOnlyList<Term> Source,       // the term, and its synonyms
    IReadOnlyList<Term> Target,       // its translations
    string? Note,                     // about the concept: definition, context, the client's instruction
    DateTimeOffset Changed,
    string? ChangedBy);

public sealed record Term(string Text, TermStatus Status = TermStatus.Unspecified)
{
    public string? Note { get; init; }  // about this term: usage, register
}

public enum TermStatus { Unspecified, Preferred, Admitted, Forbidden }
```

- **Terms are plain text.** Terms with formatting or codes are rare, and TBX holds plain text too.
- **One language pair per termbase, used in one direction**, as for a TM: the termbase knows its languages, and
  entries don't repeat them. A `de` termbase serves a `de-AT` document by the TM's rule (`TmLanguages`). Whether an
  en→hu termbase should also serve hu→en documents is [open question 11](#open-questions).
- **An entry is a concept**, as in TBX: synonyms in the source and several translations belong to one entry, so
  "folder"/"directory" → "mappa" is one entry, not two that drift apart.
- **Status** is kept from the start, though only the pane and the check use it at first: TBX marks terms as
  preferred, admitted or deprecated, and an import that dropped that would lose the client's most important
  instruction. *Unspecified* is a status of its own, so a term the client gave no status isn't exported as preferred.
  - **Translations:** a forbidden one is shown struck through, never inserted, and doesn't satisfy the check. The
    others (preferred, admitted, unspecified) are allowed. A "forbidden term used in the target" check is [open
    question 8](#open-questions).
  - **Source terms:** a forbidden source synonym is still found and marked (it still names the concept), and the
    pane shows the preferred source term next to it.

### 2. Finding terms

- **The QA check's rule:** a term is found when its folded words start the text's words, in order (`Words.FindTerm`):
  `fájl` finds `fájlt` (a Hungarian suffix), case and accents are ignored, and a term in a script without spaces
  (Chinese, Japanese, Thai, …) or with no words (`%`) is found as a folded substring. The pane, the source marks and
  the check use the same code, so they can't disagree.
- **Terms with punctuation** need a change to that rule (step 1). Today `Words` drops punctuation from a term's
  words, so `C#` and `C++` become the word `c`, found at the start of any word, and `.NET` becomes `net`. Product
  and software termbases are full of such terms. Proposed: a term containing punctuation or symbols matches only
  where the text has the same characters, folded, with only whitespace allowed to differ (`Wi-Fi` matches `wi-fi`,
  not `Wi Fi`). Apostrophes (`'`, `’`, …) count as one character, and so do hyphens (`-`, the non-breaking hyphen, …),
  since typing and typography differ (`don't` finds `don’t`). Codes are left out, as the word rule leaves them out: a code
  inside the term is skipped, and one at a gap counts as whitespace (`Wi-Fi settings` finds `<b>Wi-Fi</b> settings`).
  It still starts at a word beginning, and its last word still matches by its beginning, so suffixes
  count (`e-mail` finds `e-mailt`, `Wi-Fi` finds `Wi-Fi-t`); only a term that ends in punctuation (`C#`, `C++`)
  needs a word boundary after it, so `C#` isn't found in `C#x`. Translations are found in the target by the same rule.
  This changes the file glossary's matching the same way, which is right for both.
- **Codes:** a code's `equiv` text is part of the `QaText` the rule runs on, but a hit that starts or ends inside it
  doesn't count: terms are text, not codes. Decided in step 2: a hit counts only if its first and last characters
  are text, not a code's (its `equiv` text or a code mark); a code in the middle is fine (`Press <ph equiv="OK"/>
  now` is found for `Press OK now`). The rule applies in the source and in the target alike, so a translation only
  in the target's `equiv` doesn't satisfy the check, and to the file's glossary too. One consequence, accepted: a
  word that runs into `equiv` text is a word of both (`file` followed by the `equiv` `name` is the token
  `filename`), so `file` isn't found there. A term annotation's term is only its text: the codes in it, their
  `equiv` text included, count for nothing, so an annotation around codes only is no term and checks nothing. The index, the pane, the check and the marks all leave such hits out,
  as they all go through `QaText`'s term search.
- **Substrings start and end on characters** (step 2): a term found as a folded substring starts where a
  character of the text starts and ends where one ends, and not right before a kana voicing mark (U+3099/U+309A),
  which folding keeps. So `カ` (ka) isn't found in `ガ` (ga), whether that is one character or two, `をs` isn't in
  `をß` (half of its `ss`), and `をss` is in `をß` once, not twice. This is only the substring rule: a Latin term
  such as `s` takes the word rule, which matches a word's folded beginning, so `s` is still found in `ß` (as
  `strasse` is found in `Straße`). (The punctuation rule already started only on characters; its last
  word may still end inside a fold, as `x-Gros` finds `x-großartig`.)
- **Remaining limitations, the same as the check's:** a term isn't found inside a compound (`Datei` in
  `Textdatei`), `tab` is found in `table`, and case never matters (`US` is found in `us`). A per-term
  "case-sensitive" or "whole word" flag would fix the worst of these ([open question 7](#open-questions)). Also
  inherited from the tokenizer and folding the concordance shares, and left alone: the tokenizer treats letters
  outside the BMP (`𝐀`, CJK extension B) as punctuation, while the punctuation rule's word boundaries treat them as
  letters; folding decomposes Hangul into jamo, so `하` finds `한국`; and Thai vowel marks fold away. A word hit
  covers the whole suffixed word (`fájlt`), a punctuation or substring hit only the term (`Wi-Fi` in `Wi-Fi-t`);
  step 5 decides whether the marks should show that difference.
- **An in-memory index**, built when the termbase opens (step 2, `TermIndex`):
  - each source term is classified once (`TermPattern`: words, punctuation or substring) and filed under one key in
    one dictionary: a word term under its first folded word run (`fájl`), cut at 16 characters;
  - a term with punctuation under its first word run too, with code marks skipped (`C#` under `c`, `.NET` under
    `net`, `Wi-Fi` under `wi`). Not under its first token: `Wi-Fi` in `W<ph/>i-Fi` has the tokens `w`, `i`, `fi`,
    which a first-token key would miss, though the rule finds it;
  - terms in scripts without spaces, and terms with no words (`%`), under their first folded character. This
    replaces the short list of symbol-only terms checked every time that this section first proposed: one
    dictionary serves every kind;
  - a lookup probes, at every place of the source's folded text, its character and the beginnings of the word run
    starting there (up to the longest key), so every key a term found in the text can have is probed; the
    candidates are then confirmed by the rule itself, on the source tokenised and folded once per lookup
    (`PreparedText`, cached on the `QaText` the check reuses). The property tests check that the index finds
    exactly what trying every term of every entry finds.
- **Why in memory:** the QA check runs synchronously after every edit, on the UI thread, and has to look terms up
  there; the TM's queue answers later. Termbases are small next to TMs (thousands to tens of thousands of entries;
  the spike in step 3 measures 200,000). Writes still go to SQLite through a queue. Lookups read an immutable
  snapshot of the index, replaced after each write by one that shares everything unchanged (immutable collections),
  so a write neither blocks a lookup nor copies 200,000 entries ([open question 3](#open-questions)).
- **The result:** for each entry found, which of its source terms matched and **every** place it occurs (start and
  length in the source's `QaText`). The pane lists an entry once; the source column marks every occurrence.
  Overlapping hits are all kept (`file` and `file manager`), longest first.

### 3. Storage

- **A SQLite file** per termbase (`*.sctb`), as for the TM: one file to copy and back up, crash-safe, through the
  same `Microsoft.Data.Sqlite`.
- **Tables:** `meta` (format `SchrodingersCat.TB`, version, languages), `entries` (id, note, changed, changed by)
  and `terms` (entry id, side, text, status, note, order). Ids work as the TM's do: they increase and are never
  reused, so the editor can point at an entry.
- **Untrusted files**, as for the TM: `Open` requires exactly the expected tables and indexes (no triggers or views
  of the file's own), the format and version in `meta`, sets `trusted_schema=OFF`, doesn't pool connections, and
  anything else, or a damaged entry found later, throws a format exception.
- **Loading** reads every entry into the index when the termbase opens, on a background thread, at startup too.
  Until it's done the terms list shows "Loading…", the source has no marks, and the check uses the file's glossary
  only; when it's done, the checks re-run.

### 4. Exchange

- **TBX import**, both versions in use. The element paths:
  - TBX 2 (ISO 30042:2008, root `<martif>`, no namespace): `termEntry` → `langSet` (`xml:lang`) → `tig` → `term`,
    or `ntig` → `termGrp` → `term`. A term's status and note sit beside it in the `tig` or `termGrp`.
  - TBX 3 (ISO 30042:2019, root `<tbx>`, namespace `urn:iso:std:iso:30042:ed-2`, dialect in `type`): `conceptEntry`
    → `langSec` → `termSec` → `term`. Both styles are read for the data categories below: DCA (`termNote
    type="administrativeStatus"`) and DCT (an element per data category, in its module's namespace:
    `administrativeStatus` from the Min module, `http://www.tbxinfo.net/ns/min`; `definition` from Basic,
    `http://www.tbxinfo.net/ns/basic`). DCT elements are matched by namespace URI and local name, never by prefix.
    Other data categories are dropped and counted in the report.
- **Status:**

  | TBX data category | Values | Status |
  |---|---|---|
  | `administrativeStatus` | `preferredTerm-admn-sts` | Preferred |
  | | `admittedTerm-admn-sts` | Admitted |
  | | `deprecatedTerm-admn-sts`, `supersededTerm-admn-sts` | Forbidden |
  | `normativeAuthorization` | `preferredTerm`, `standardizedTerm`, `legalTerm`, `regulatedTerm` | Preferred |
  | | `admittedTerm` | Admitted |
  | | `deprecatedTerm`, `supersededTerm` | Forbidden |
  | neither, or another value | | Unspecified (another value is reported) |

- **Notes:** `note`, and the definition (`descrip type="definition"` in DCA, the Basic module's `definition` in DCT), at concept level and in the two languages' `langSet`/`langSec`
  become the entry's `Note`, joined in document order, each language's prefixed with its tag; at term level, the
  term's `Note`.
- **Languages:** the termbase's two languages are taken, regional variants included by the TM's rule; an entry
  without both is skipped and reported, as TMX import does. Other languages in an entry are dropped.
- **TBX export:** TBX 2 with TBX-Basic's data categories (`martif`, no DOCTYPE), the version most tools import;
  TBX 3 as an option ([open question 10](#open-questions)). *Unspecified* writes no status. Step 4 checks the export
  once by hand in at least one other tool, not only by importing it back.
- **CSV import:** clients' spreadsheets, so the import adapts to them:
  - **Encoding:** a UTF-8 or UTF-16 BOM decides; without one, strict UTF-8. Bytes that aren't valid UTF-8 (Excel's
    default CSV is in the system's legacy code page, e.g. Windows-1250 for Hungarian) are never replaced: the
    import dialog asks for the encoding and shows the first rows decoded with it.
  - **Columns:** the dialog shows the header row and the first rows, and the user says which column is the source,
    the target, the note and the status. Headers that are language tags (`en`, `hu`) or language names
    ("English") are recognised as a starting point.
  - **Format:** RFC 4180 (quoted fields, doubled quotes, line breaks inside quotes). The field separator (`,`, `;`
    or tab) is detected from the header row. Several terms in one cell are separated by `|`, which isn't a field
    separator.
- **CSV export:** UTF-8 with BOM (so Excel reads it as UTF-8), every field quoted, `|` between terms. A field that
  starts with `=`, `+`, `-` or `@` gets a leading `'`, so a spreadsheet doesn't run it as a formula; the import
  strips a `'` only when one of those four follows it, so a client's own `'til` stays. CSV holds one status and one
  note per row, and `|` can't be inside a term, so terms with different statuses, their own notes, or a `|` are
  exported without what CSV can't hold (a term with `|` is left out), and the export says how many; only TBX keeps
  everything.
- **Defences**, as for TMX: a size limit (`LimitedReadStream`), a depth limit (`DepthLimitingXmlReader`), no
  external resolver, and failures as the termbase's format exception; malformed entries are skipped and reported
  instead of failing the import. One difference: TBX 2 files almost always start with
  `<!DOCTYPE martif SYSTEM "TBXcoreStructV02.dtd">`, which the TMX and XLIFF readers refuse. The TBX reader skips the
  DOCTYPE without processing it (`DtdProcessing.Ignore`), so no entity is ever expanded and nothing is fetched, and an
  entity the DTD would have defined fails as malformed XML ([open question 9](#open-questions)).

### 5. QA

The terminology check today reads `GlossaryEntry(Term, Translations)` from the unit's glossary module, merges
entries by folded term, and says "The glossary translates …". Step 1 changes it, before the termbase exists:

- **A term record with an origin:** Core gains a record for what the check compares, with the source terms
  (synonyms), the allowed translations, the forbidden translations, and the origin (the file's glossary, or a named
  termbase). `GlossaryReader`'s entries become such records with the file as origin.
- **A term source per segment:** `ITermSource { IReadOnlyList<…> Find(QaText source); }`, looked up lazily per
  segment in `QaSegment` (which has the source's `QaText`), not in the unit context, which is built once per unit.
- **Not in `QaOptions`:** those are the user's settings, rebuilt by the QA settings dialog (`ToOptions`) and saved;
  a term source there would be dropped on the next save. It goes to `QaChecker.Check(unit, options, terms)` as its
  own argument, and `UnitCheck` gets it from the window next to the options.
- **Merging:** records found for the same folded term are checked as one, whatever their origin or concept, with
  all their allowed translations; the message names every origin ("The termbase *client* translates …", "The file's
  glossary and the termbase *client* translate …"). A concept with several synonyms in the source is reported once.
  Homonyms (two concepts for "file", noun and verb) therefore pass with either translation; the pane still lists
  them apart.
- **Forbidden translations** never satisfy the check. A record with no allowed translation reports nothing until the
  forbidden-term check exists ([open question 8](#open-questions)).
- **Re-running:** opening, closing or loading the termbase re-runs every unit's checks; not through `UseQaOptions`,
  which saves settings. Adding or editing a term re-checks only the units whose source contains the term's source
  terms (found with the same rule), so Ctrl+E doesn't re-check the whole document.

### 6. In the editor

- **Terms list:** in the Matches tab, below the matches, so terms and matches are seen together while translating
  (the Concordance and QA tabs are for looking things up). Each row: the source term found, its translations
  (forbidden ones struck through), the notes as a tooltip, and an Insert button; the termbase's name and languages
  in the header, as for the TM ([open question 5](#open-questions)).
- **Marking terms:** every occurrence of a found term gets a solid underline in the source column, unlike the dotted
  underline of a term annotation (`<mrk type="term">`), with the translations in a tooltip. A new `AnnotationStyle`
  value, as term annotations are styled. The ranges need a new `QaText` method that maps a range to every element
  piece it covers (`SpanOf` returns only the first), so `ContentRuns` can split text runs at them. Chips inside a
  hit aren't underlined (hits inside `equiv` text don't exist, see [finding terms](#2-finding-terms)). Marks are looked
  up for the rows the list shows and cached per segment until the termbase changes.
- **Inserting:** Ctrl+T opens a picker at the caret listing the segment's terms (type-ahead, Up/Down, Enter,
  Escape), like the code picker (Ctrl+Shift+F9); the translation goes in as text through `TargetEditModel.Paste`,
  as the concordance inserts. Ctrl+1 … Ctrl+9 stay the matches' ([open question 6](#open-questions)).
- **Adding a term:** Ctrl+E (memoQ's key) opens a small dialog with the selected source text and the selected target
  text filled in, notes and statuses; it lists the entries that already have that source term, and offers to add
  the translation to one of them or to make a new entry. The new term shows in the list and the source at once.
- **Editing and deleting:** from a row's context menu, in the same dialog. Deleting asks first; there is no undo.
- **Choosing the termbase:** a "Termbase" menu like the TM's: New…, Open…, Close, Import TBX/CSV…, Export TBX/CSV….
  One termbase open at a time, remembered between sessions in `AppSettings` (a new `TermbasePath`, which old
  settings files simply lack) ([open question 4](#open-questions)).

## Out of scope

- Several termbases at once, or a project's termbases (see [open question 4](#open-questions)).
- Server or shared termbases.
- Term extraction (finding term candidates in documents or a TM).
- Morphology beyond the word-beginning rule: stemming, compounds, prefixes.
- Concept relations, subject fields, images, and the other TBX data categories beyond terms, status and notes (an
  import drops them, and says so).
- Trados MultiTerm's own `.sdltb` format (convert it to TBX first).
- Writing terms into the XLIFF file's glossary module (the file's own glossary stays read-only).

## Implementation steps

Each step ends with passing tests, zero warnings and green CI, and can be merged on its own.

1. **Core: the check and the rule.** The term record with origin and forbidden translations, `ITermSource` as its
   own argument to `QaChecker.Check`, per-segment lookup, messages naming origins, and the punctuation rule in
   `Words.FindTerm`.
   - Examples: a termbase record alone, the same term from both origins, synonyms, homonyms, a forbidden
     translation, a record with only forbidden translations, `C#`, `C++`, `.NET`, `Wi-Fi`, and their suffixed forms (`e-mail` → `e-mailt`, `Wi-Fi` → `Wi-Fi-t`) in sources and targets.
   - The property tests' rules hold with a term source: arbitrary targets never throw, and a target containing an
     allowed translation reports nothing for it.
2. **Entries and the index:** `TermEntry`, `Term`, the in-memory index, finding every occurrence, and `QaText`'s
   mapping of a range to element pieces.
   - Examples per rule: suffixes, case and accents, scripts without spaces, mixed scripts (`このprofileを開く`),
     symbol terms, `C#`, `C++`, `.NET`, `Wi-Fi`, overlapping terms, a hit across a code, and a term that occurs only
     in `equiv` text (not found).
   - Properties: every hit's range is inside the text and the term rule finds the term there; the index finds what
     a scan of every entry finds, with generated terms that include punctuation.
3. **Storage spike and SQLite termbase:** `Create`, `Open`, add, update, delete, load into the index.
   - The spike measures a generated termbase of 200,000 entries and a document of 5,000 segments, against these
     budgets: loading under 2 s; one segment's terms p95 under 5 ms; adding a term, with its re-check, under 50 ms;
     re-checking the whole document under 1 s; the index under 200 MB. If the design misses one, the step records
     why and what was chosen instead.
   - Untrusted-file tests as for the TM; a property that random edit scripts keep the file and an in-memory termbase
     equal.
4. **Exchange:** TBX 2 and 3 import (DCA and DCT), TBX 2 export (TBX 3 as an option), CSV import and export.
   - Sample files of every variant, from other tools where their licences allow; round trips through TBX keep
     entries, status (Unspecified included) and notes; CSV round trips keep what CSV can hold; damaged files fail
     only with the format exception; a DOCTYPE is skipped, never processed; legacy encodings, formula prefixes, a client term starting with `'`, and a term with `|` (reported).
   - The export checked by hand in at least one other tool, with the result recorded here.
5. **App: the termbase in the editor.** The Termbase menu, remembering and loading it, the terms list, the source
   marks, Ctrl+T, and the checks using it.
   - View-model tests with real `.sctb` files; headless tests for the list, the marks, the picker and inserting.
6. **App: adding and editing terms.** Ctrl+E, the entry dialog, editing and deleting from the list, re-checking
   only the affected units.
   - View-model and headless tests, including adding a translation to an existing entry.
7. **Documentation:** CLAUDE.md, and mark this proposal implemented.

## Open questions

1. **Where does it live?**
   - Option (a): in `SchrodingersCat.Memory` (proposed): it has SQLite, the untrusted-file defences, and access to
     `Words`.
   - Option (b): a new `SchrodingersCat.Terms` project: cleaner names, but it would need its own SQLite reference,
     and Core's `InternalsVisibleTo` for `Words` and the XML defences.

   **Decision (2026-10-10):** (a), in `SchrodingersCat.Memory`.
2. **What is an entry?**
   - Option (a): a bilingual concept (proposed): synonyms and several translations, with status and notes.
   - Option (b): one source term and one translation per entry: simplest, but synonyms become separate entries, and
     TBX concepts have to be split on import.
   - Option (c): multilingual concepts, any number of languages, as TBX allows: one termbase for every target
     language, but the editor only ever needs two, and every query has to pick them.

   **Decision (2026-10-10):** (a), a bilingual concept.
3. **Lookups from memory or from SQLite?**
   - Option (a): an in-memory index, loaded at open (proposed): synchronous lookups for the QA check, no SQL per
     segment. Costs memory and loading time, which the spike measures.
   - Option (b): SQL queries per lookup: nothing to load, but the QA check would have to become asynchronous, or
     query on the UI thread.

   **Decision (2026-10-10):** (a), an in-memory index loaded at open; the step 3 spike checks the budgets.
4. **How many termbases?**
   - Option (a): one at a time, remembered between sessions (proposed), as decided for the TM.
   - Option (b): several at once, in priority order (a client's and the translator's own): what many translators
     want, but it needs a list in the settings, origins on every hit, and a choice of where Ctrl+E adds. The check's
     origins (step 1) already allow it later.

   **Decision (2026-10-10):** (a), one at a time, remembered between sessions.
5. **Where are terms shown?**
   - Option (a): a list in the Matches tab, below the matches (proposed): seen while translating.
   - Option (b): a Terms tab of its own: more room, but hidden whenever the matches show.
   - Option (c): one list with the matches, numbered together (as memoQ does): one place, but Ctrl+1 … Ctrl+9 would
     mean something different in every segment.

   **Decision (2026-10-10):** (a), a list in the Matches tab, below the matches.
6. **Which key inserts a term?**
   - Option (a): Ctrl+T, then a picker (proposed): free, and like the code picker.
   - Option (b): Alt+1 … Alt+9: one key, but Option+digit types characters on macOS (`¡`, `™`, …), which the manual
     checks require to work.
   - Option (c): Ctrl+Alt+1 … Ctrl+Alt+9: Ctrl+Alt is AltGr on Windows, and types characters on many layouts.

   **Decision (2026-10-10):** (a), Ctrl+T, then a picker.
7. **Case and whole words.** Proposed: the QA check's rule for every term (case and accents ignored, word
   beginnings), with the punctuation rule above, so that the pane and the check agree. A per-term flag
   ("case-sensitive", "whole word only") would fix acronyms (`US`) and short terms (`tab` in `table`), at the cost of
   a second rule in both places.

   **Decision (2026-10-10):** as proposed: one rule for every term, no per-term flags.
8. **Forbidden terms in the target.** Proposed: keep the status from the start, show forbidden translations struck
   through, never insert them or let them satisfy the check, and leave a "forbidden term used" check for a later
   step. Option: add it to step 1 (a Warning, like the terminology check).

   **Decision (2026-10-10):** as proposed: keep the status from the start; the forbidden-term check comes in a later
   step.
9. **TBX files with a DOCTYPE.** Proposed: skip it without processing (`DtdProcessing.Ignore`), because most TBX 2
   files have one. Option: refuse them, as the TMX and XLIFF readers do, and ask users to delete the line.

   **Decision (2026-10-10):** as proposed: skip the DOCTYPE without processing it.
10. **Formats.** Proposed: import TBX 2 and 3 (DCA and DCT) and CSV; export TBX 2 (TBX-Basic) and CSV, with TBX 3
    as an option. Option: refuse DCT files with a clear message, if few tools write them.

    **Decision (2026-10-10):** as proposed.
11. **Both directions?** Proposed: no; an en→hu termbase serves en→hu documents only, as a TM does, and the reverse
    needs a second termbase (an export and import away). Option: let a termbase serve the reverse direction too
    (terminology is concept-based and TBX has no direction): one termbase for both, but the source and target
    roles, synonyms' statuses and the index would all have to work either way round.

    **Decision (2026-10-10):** as proposed: one direction only.
