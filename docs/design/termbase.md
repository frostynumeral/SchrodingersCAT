# Proposal: termbase

**Status:** proposed (2026-10-08). The [open questions](#open-questions) need deciding before step 1 starts.

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

- **Store** term entries for one language pair: a source term, its translations, optionally synonyms, a note, and
  whether a translation is preferred or forbidden.
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

1. **Matching** (`SchrodingersCat.Memory`, no storage, no UI): entries and finding them in a text, over an
   in-memory index.
2. **Storage** (same project): a termbase file on disk, loaded into that index.
3. **Exchange** (same project): TBX and CSV import and export.
4. **QA** (`SchrodingersCat.Core`): the terminology check takes term entries from outside the file.
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
    string? Note,                     // definition, context, the client's instruction
    DateTimeOffset Changed,
    string? ChangedBy);

public sealed record Term(string Text, TermStatus Status = TermStatus.Preferred);

public enum TermStatus { Preferred, Admitted, Forbidden }
```

- **Terms are plain text.** Terms with formatting or codes are rare, and TBX holds plain text too. Matching works on
  the source's `QaText` (codes as their `equiv` or a mark), so a term found there maps back to a place in the
  content with `QaText.SpanOf`, as the QA check's spans already do.
- **One language pair per termbase**, as for a TM: the termbase knows its languages, and entries don't repeat them.
  A `de` termbase serves a `de-AT` document by the TM's rule (`TmLanguages`).
- **An entry is a concept**, as in TBX: synonyms in the source and several translations belong to one entry, so
  "folder"/"directory" → "mappa" is one entry, not two that drift apart.
- **Status** is kept from the start, even though only the pane uses it at first: a TBX file marks terms as
  preferred, admitted or deprecated, and an import that dropped that would lose the client's most important
  instruction. A forbidden translation is shown struck through and never inserted; a check for forbidden terms in the
  target is [open question 8](#open-questions).

### 2. Finding terms

- **The QA check's rule:** a term is found when its folded words start the text's words, in order (`Words.FindTerm`):
  `fájl` finds `fájlt` (a Hungarian suffix), case and accents are ignored, and a term in a script without spaces
  (Chinese, Japanese, Thai, …) or with no words (`%`) is found as a folded substring. The pane, the source marks and
  the check use the same function, so they can't disagree.
- **Limitations, the same as the check's:** a term isn't found inside a compound (`Datei` in `Textdatei`), `tab` is
  found in `table`, and case never matters (`US` is found in `us`). A per-term "case-sensitive" or "whole word"
  flag would fix the worst of these; it's [open question 7](#open-questions).
- **An in-memory index**, built when the termbase opens and kept up to date as terms are added:
  - word terms by their first folded word; a source word looks up its own prefixes (`f`, `fá`, `fáj`, `fájl`,
    `fájlt`), so the cost is per word of the source, not per entry;
  - terms in scripts without spaces by their first folded character;
  - terms with no words in a short list checked every time;
  - each candidate is then confirmed with `FindTerm`.
- **Why in memory:** the QA check runs synchronously after every edit, on the UI thread, and has to look terms up
  there; the TM's queue answers later. Termbases are small next to TMs (thousands to tens of thousands of entries;
  the spike in step 3 measures 200,000), so holding the index costs little. Writes still go to SQLite through a
  queue; the index is an immutable snapshot replaced after each write, so lookups never wait for one ([open question
  3](#open-questions)).
- **The result** is a list of hits: the entry, which of its source terms was found, and where (start and length in
  the `QaText`). An entry found several times is listed once, at its first place; overlapping hits are all kept
  (`file` and `file manager`), longest first.

### 3. Storage

- **A SQLite file** per termbase (`*.sctb`), as for the TM: one file to copy and back up, crash-safe, through the
  same `Microsoft.Data.Sqlite`.
- **Tables:** `meta` (format `SchrodingersCat.TB`, version, languages), `entries` (id, note, changed, changed by)
  and `terms` (entry id, side, text, status, order). Ids work as the TM's do: they increase and are never reused, so
  the editor can point at an entry.
- **Untrusted files**, as for the TM: `Open` requires exactly the expected tables and indexes (no triggers or views
  of the file's own), the format and version in `meta`, sets `trusted_schema=OFF`, doesn't pool connections, and
  anything else, or a damaged entry found later, throws a format exception.
- **Loading** reads every entry into the index when the termbase opens, on a background thread; the terms list shows
  "Loading…" until it's done.

### 4. Exchange

- **TBX import**, both versions in use:
  - TBX 2 (ISO 30042:2008, root `<martif>`, the version most tools still export): `termEntry` →
    `langSet` (`xml:lang`) → `tig`/`ntig` → `term`, with `termNote type="administrativeStatus"` giving the status
    (`deprecatedTerm-admn-sts` and `supersededTerm-admn-sts` become forbidden) and `note`/`descrip type="definition"`
    the note.
  - TBX 3 (ISO 30042:2019, root `<tbx>`, DCA style): `conceptEntry` → `langSec` → `termSec`, with the same data
    categories.
  - **Languages:** the termbase's two languages are taken, regional variants included by the TM's rule; an entry with
    neither, or only one, is skipped and reported, as TMX import does. Other languages in an entry are dropped.
- **TBX export:** TBX 3, TBX-Basic dialect, which current tools import ([open question 10](#open-questions)).
- **CSV import and export:** a header row names the columns by language tag (`en`, `hu`), with optional `note`
  and `status` columns; several terms in one cell are separated by `;`. UTF-8, with or without BOM; the separator
  (`,`, `;` or tab) is detected from the header.
- **Defences**, as for TMX: a size limit (`LimitedReadStream`), a depth limit (`DepthLimitingXmlReader`), no
  external resolver, and failures as the termbase's format exception; malformed entries are skipped and reported
  instead of failing the import. One difference: TBX 2 files almost always start with
  `<!DOCTYPE martif SYSTEM "TBXcoreStructV02.dtd">`, which the TMX and XLIFF readers refuse. The TBX reader skips the
  DOCTYPE without processing it (`DtdProcessing.Ignore`), so no entity is ever expanded and nothing is fetched, and an
  entity the DTD would have defined fails as malformed XML ([open question 9](#open-questions)).

### 5. QA

- **A term source** goes into `QaOptions` (Core can't reference Memory): an interface such as
  `ITermSource { IReadOnlyList<GlossaryEntry> EntriesFor(QaText source); }`, which the app implements over the
  termbase. `QaSegment`'s unit context merges its entries with the file's glossary, so the existing merging (entries
  for the same folded term count as one, with all their translations) covers both.
- **Messages say where a term comes from:** "The termbase translates …" or "The glossary translates …"; an entry
  found in both is reported once, naming both.
- **Forbidden translations** take no part in the existing check (a target that has only a forbidden translation
  doesn't pass).
- **Re-running:** opening, closing or changing the termbase re-runs every unit's checks, as changing the QA settings
  does.

### 6. In the editor

- **Terms list:** in the Matches tab, below the matches, so terms and matches are seen together while translating
  (the Concordance and QA tabs are for looking things up). Each row: the source term found, its translations
  (forbidden ones struck through), the note as a tooltip, and an Insert button; the termbase's name and languages
  in the header, as for the TM ([open question 5](#open-questions)).
- **Marking terms:** found terms get a solid underline in the source column, unlike the dotted underline of a term
  annotation (`<mrk type="term">`), with the translations in a tooltip. That needs a new flag on `ContentRun`
  (`ContentRuns` splits text runs at the found ranges).
- **Inserting:** Ctrl+T opens a picker at the caret listing the segment's terms (type-ahead, Up/Down, Enter,
  Escape), like the code picker (Ctrl+Shift+F9); the translation goes in as text through `TargetEditModel.Paste`,
  as the concordance inserts. Ctrl+1 … Ctrl+9 stay the matches' ([open question 6](#open-questions)).
- **Adding a term:** Ctrl+E (memoQ's key) opens a small dialog with the selected source text and the selected target
  text filled in, a note and a status; it lists the entries that already have that source term, and offers to add
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

1. **Core: a term source for the QA check.** `ITermSource` in `QaOptions`, merged with the file's glossary in the
   unit context; messages naming the source; forbidden translations not passing.
   - Examples: a termbase entry alone, the same term in both, a forbidden translation, a source without hits.
   - The property tests' rules hold with a term source: arbitrary targets never throw, and a target containing an
     entry's translation reports nothing for it.
2. **Matching:** `TermEntry`, `Term`, the in-memory index and finding hits.
   - Examples per rule: suffixes, case and accents, scripts without spaces, mixed scripts (`このprofileを開く`),
     symbol terms, overlapping terms, synonyms.
   - Properties: every hit's range is inside the text and `FindTerm` finds the same term there; the index finds what
     a scan of every entry with `FindTerm` finds.
3. **Storage spike and SQLite termbase:** `Create`, `Open`, add, update, delete, load into the index.
   - The spike measures a generated termbase of 200,000 entries: loading (budget: under 2 s) and finding a
     segment's terms (budget: p95 under 5 ms). If the index misses either, the step records why and what was chosen
     instead.
   - Untrusted-file tests as for the TM; a property that random edit scripts keep the file and an in-memory termbase
     equal.
4. **Exchange:** TBX 2 and TBX 3 import, TBX 3 export, CSV import and export.
   - Sample files exported by other tools, as far as their licences allow; round trips (export then import keeps
     entries, status and notes); damaged files fail only with the format exception; a DOCTYPE is skipped, never
     processed.
5. **App: the termbase in the editor.** The Termbase menu, remembering it, the terms list, the source marks, Ctrl+T,
   and the QA check using it.
   - View-model tests with real `.sctb` files; headless tests for the list, the marks, the picker and inserting.
6. **App: adding and editing terms.** Ctrl+E, the entry dialog, editing and deleting from the list.
   - View-model and headless tests, including adding a translation to an existing entry.
7. **Documentation:** CLAUDE.md, and mark this proposal implemented.

## Open questions

1. **Where does it live?**
   - Option (a): in `SchrodingersCat.Memory` (proposed): it has SQLite, the untrusted-file defences, and access to
     `Words`.
   - Option (b): a new `SchrodingersCat.Terms` project: cleaner names, but it would need its own SQLite reference,
     and Core's `InternalsVisibleTo` for `Words` and the XML defences.
2. **What is an entry?**
   - Option (a): a bilingual concept (proposed): synonyms and several translations, with status and a note.
   - Option (b): one source term and one translation per entry: simplest, but synonyms become separate entries, and
     TBX concepts have to be split on import.
   - Option (c): multilingual concepts, any number of languages, as TBX allows: one termbase for every target
     language, but the editor only ever needs two, and every query has to pick them.
3. **Lookups from memory or from SQLite?**
   - Option (a): an in-memory index, loaded at open (proposed): synchronous lookups for the QA check, no SQL per
     segment. Costs memory and loading time, which the spike measures.
   - Option (b): SQL queries per lookup: nothing to load, but the QA check would have to become asynchronous, or
     query on the UI thread.
4. **How many termbases?**
   - Option (a): one at a time, remembered between sessions (proposed), as decided for the TM.
   - Option (b): several at once, in priority order (a client's and the translator's own): what many translators
     want, but it needs a list in the settings, origins on every hit, and a choice of where Ctrl+E adds.
5. **Where are terms shown?**
   - Option (a): a list in the Matches tab, below the matches (proposed): seen while translating.
   - Option (b): a Terms tab of its own: more room, but hidden whenever the matches show.
   - Option (c): one list with the matches, numbered together (as memoQ does): one place, but Ctrl+1 … Ctrl+9 would
     mean something different in every segment.
6. **Which key inserts a term?**
   - Option (a): Ctrl+T, then a picker (proposed): free, and like the code picker.
   - Option (b): Alt+1 … Alt+9: one key, but Option+digit types characters on macOS (`¡`, `™`, …), which the manual
     checks require to work.
   - Option (c): Ctrl+Alt+1 … Ctrl+Alt+9: Ctrl+Alt is AltGr on Windows, and types characters on many layouts.
7. **Case and whole words.** Proposed: the QA check's rule for every term (case and accents ignored, word
   beginnings), so that the pane and the check agree. A per-term flag ("case-sensitive", "whole word only") would
   fix acronyms (`US`) and short terms (`tab` in `table`), at the cost of a second rule in both places.
8. **Forbidden terms in the target.** Proposed: keep the status from the start, show forbidden translations struck
   through and never insert them, and leave a "forbidden term used" check for a later step. Option: add it to step 1
   (a Warning, like the terminology check).
9. **TBX files with a DOCTYPE.** Proposed: skip it without processing (`DtdProcessing.Ignore`), because most TBX 2
   files have one. Option: refuse them, as the TMX and XLIFF readers do, and ask users to delete the line.
10. **Formats.** Proposed: import TBX 2 and 3 and CSV; export TBX 3 (TBX-Basic) and CSV. Option: export TBX 2 as
    well, for tools that don't read TBX 3 yet.
