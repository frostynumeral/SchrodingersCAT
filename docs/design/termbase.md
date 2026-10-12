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
  - a term with punctuation under its characters up to its first space, folded, apostrophes and hyphens as one
    (`C#` under `c#`, `.NET` under `.net`, `Wi-Fi settings` under `wi-fi`), cut at 16 characters too. Step 2 filed
    these under their first word run (`.NET` under `net`); the step 3 spike changed it (see below), because the
    spike's 1,600 terms starting `e-` all landed under `e`, tried at every word beginning with an `e`;
  - terms in scripts without spaces, and terms with no words (`%`), under their first folded character. This
    replaces the short list of symbol-only terms checked every time that this section first proposed: one
    dictionary serves every kind;
  - a lookup probes each of the source's words with the beginnings of its word run (up to the longest key), and
    each place of its folded text with its character and, where a word may begin, the beginnings of its characters
    up to whitespace, code marks skipped as the rule skips them (so `Wi-Fi` is found in `W<ph/>i-Fi`); so every key
    a term found in the text can have is probed. Keys are 64-bit hashes, and a bit per hash rules out most probes
    without a lookup. The candidates are then confirmed by the rule itself, on the source tokenised and folded once
    per lookup (`PreparedText`, cached on the `QaText` the check reuses). The property tests check that the index
    finds exactly what trying every term of every entry finds.
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
- **Tables** (decided in step 3; `STRICT`, so a value of the wrong type can't be stored through SQLite):

  ```sql
  CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT, WITHOUT ROWID
  CREATE TABLE entries(id INTEGER PRIMARY KEY, note BLOB, changed INTEGER NOT NULL, changed_by BLOB) STRICT
  CREATE TABLE terms(entry INTEGER NOT NULL, side INTEGER NOT NULL, position INTEGER NOT NULL, text BLOB NOT NULL,
      status INTEGER NOT NULL, note BLOB, PRIMARY KEY(entry, side, position)) STRICT, WITHOUT ROWID
  ```

  - `meta` holds `format` (`SchrodingersCat.TB`), `version` (1), `source_language` and `target_language`, and, once
    entries were deleted, `last_deleted_id` and `deleted_since_compact` (digits only).
  - `terms` is stored in the order of its key, so loading reads it in one pass beside `entries`, and an entry's
    terms are one range of it. `side` is 0 for the source, 1 for the target; `position` counts from 0 within a side
    (`order` is an SQL keyword); `status` is `(int)TermStatus`, pinned by a test.
  - Strings (terms, notes, the user) are raw UTF-16 BLOBs, so nothing is lost (lone surrogates, U+0000, U+FFFF); an
    empty BLOB is the empty string, `NULL` is null. They are never searched in SQL: every lookup is the index's. Not
    the TM's content format, which would build an `InlineContent` per string while loading.
  - `changed` is UTC ticks; both termbases store `Changed` as UTC, so a snapshot after a write equals one after
    reopening.
  - Ids work as the TM's do: they increase and are never reused (the highest id deleted is kept in `meta`), so the
    editor can point at an entry.
- **Untrusted files**, as for the TM: `Open` requires exactly the expected tables, compared with their SQL text (no
  triggers, views or indexes of the file's own, and no table changed by `ALTER TABLE`), the format, version and
  languages in `meta` (stored as text) and no other keys, sets `trusted_schema=OFF`, doesn't pool connections, and checks every value
  it loads; anything else throws `TermbaseFormatException`, the termbase's format exception.
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

1. **Core: the check and the rule.** *(Done 2026-10-12.)* The term record with origin and forbidden translations, `ITermSource` as its
   own argument to `QaChecker.Check`, per-segment lookup, messages naming origins, and the punctuation rule in
   `Words.FindTerm`.
   - Examples: a termbase record alone, the same term from both origins, synonyms, homonyms, a forbidden
     translation, a record with only forbidden translations, `C#`, `C++`, `.NET`, `Wi-Fi`, and their suffixed forms (`e-mail` → `e-mailt`, `Wi-Fi` → `Wi-Fi-t`) in sources and targets.
   - The property tests' rules hold with a term source: arbitrary targets never throw, and a target containing an
     allowed translation reports nothing for it.
2. **Entries and the index:** *(Done 2026-10-12.)* `TermEntry`, `Term`, the in-memory index, finding every occurrence, and `QaText`'s
   mapping of a range to element pieces.
   - Examples per rule: suffixes, case and accents, scripts without spaces, mixed scripts (`このprofileを開く`),
     symbol terms, `C#`, `C++`, `.NET`, `Wi-Fi`, overlapping terms, a hit across a code, and a term that occurs only
     in `equiv` text (not found).
   - Properties: every hit's range is inside the text and the term rule finds the term there; the index finds what
     a scan of every entry finds, with generated terms that include punctuation.
3. **Storage spike and SQLite termbase:** *(Done 2026-10-12.)* `Create`, `Open`, add, update, delete, load into the
   index.
   - The spike measures a generated termbase of 200,000 entries and a document of 5,000 segments, against these
     budgets: loading under 2 s; one segment's terms p95 under 5 ms; adding a term, with its re-check, under 50 ms;
     re-checking the whole document under 1 s; the index under 200 MB. If the design misses one, the step records
     why and what was chosen instead.
   - Untrusted-file tests as for the TM; a property that random edit scripts keep the file and an in-memory termbase
     equal.
   - **Spike** (throwaway, not kept): 200,000 entries in one `AddRange`. Source words from a vocabulary of 200,000
     pseudo-words of 2–4 syllables (16 consonants × 5 vowels; a fifth with á, é, ő or ß), drawn by Zipf (s = 1),
     a term's words from rank 100 on (function words seldom start a term). Source terms of 1 word 45%, 2 words
     35%, 3 words 14%, 4–6 words 6%; 5% with punctuation (`X-Y`, `X.Y`, `.X`, `X#`, `X++`, `X/Y`, `X's`, `e-X`), 3%
     without spaces (katakana, hanzi, Thai), 0.1% symbols, 1% with a first word over 16 characters, 2% homonyms; 1–4
     synonyms, 0–3 translations, every status, notes on a fifth of the entries and on 5% of the terms, 5 users,
     dates over two years. The document: 5,000 segments in 1,448 units, 3–27 words (10% 80–120; 24.7 on average)
     with numbers and commas; 35% with 1–3 of the termbase's terms (30% with a suffix, 10% in upper case with a
     typographic apostrophe or hyphen), 20% with codes, 5% Japanese; 70% translated, half of those with the term's
     translation. A segment has 22.9 hits on average (0.93 per word): pessimistic, as nearly every content word is
     a term. Adding a term: 100 adds (the document's 50 most frequent content words, 30 rare words, 20 with
     punctuation or in katakana), each timed from the commit to the re-check of the units whose source has it.
     Release build, i5-12400 (6 cores, 12 threads), NVMe SSD, Linux 7.0, .NET 10.0.12, SQLite 3.53.3, workstation
     GC, the file in the page cache; memory is the growth of `GC.GetTotalMemory` after full collections.

     | Version | Loading, median / p95 | One segment's terms, p50 / p95 | Adding a term + re-check, p50 / p95 | Re-checking the document | Index |
     |---|---|---|---|---|---|
     | Budget | 2 s | p95 5 ms | 50 ms | 1 s | 200 MB |
     | First generator, the code as first measured | 2,007 / 2,082 ms | 0.21 / 0.98 ms | 51 / 1,519 ms¹ | 1,835 ms | 193 MB |
     | Corrected generator, same code | 2,077 / 2,131 ms | 0.13 / 0.60 ms | 50 / 136 ms | 1,185 ms | 192 MB |
     | + keys of terms with punctuation up to their first space, a bit per key, a phrase's second word compared first, records kept with the index | 2,059 / 2,144 ms | 0.036 / 0.19 ms | 29 / 73 ms | 489 ms | 196 MB |
     | + loading reads each row in one call and classifies entries while it reads | 1,720 / 1,753 ms | 0.034 / 0.18 ms | 40 / 77 ms | 475 ms (257 ms in parallel) | 189 MB |
     | + re-checking as step 6 will (below) | | | 14 / 51 ms (36 ms without the commit) | | |

     ¹ Adding the document's 50 most frequent words; see the corrections below.

     Memory after use (measured after review, with the spike's termbase file and a document generated to the same
     description, 20.8 hits per segment): the rows above measured the index right after loading, before any lookup.
     The fifth row kept each entry's check record with the index once a lookup had found it (its lists, and each term
     and translation trimmed, keyed and classified), and the index's entries are shared by every later snapshot, so
     the records stayed while the termbase was open: one check of the document found 25,443 entries and added 19.5 MB
     (about 770 bytes a record; 209 MB in all), and a record for every entry would add 190 MB (380 MB). Keeping at most
     4,096 records (a slot per entry's hash) bounded that, but the document's check thrashed it: 650 ms, as slow as
     keeping none (600 ms, against 400 ms with every record kept; this document's check is slower than the spike's in
     the table, so compare these three only with each other). So the term source keeps the records of the entries it
     found, and the index none: the index is 187 MB (937 bytes per entry, the record's field gone) however long it is
     open, re-checking takes as long as with records kept, and the records go with the source (21 MB for this document,
     the dictionary's nodes included). A new snapshot's source takes over the previous one's records
     (`ToTermSource`'s `previous`; an entry changed since gets a new record): re-checking in full the units a new term
     affects (100 adds as in the table, 40 units on average) took p95 70 ms with a fresh source, and 38 ms with the
     records taken over, as with records kept in the index.

     Other timings: the import of 200,000 entries 2.6 s (a 48.7 MB file); one update 5 ms; deleting 20,000 entries
     in one call 0.33 s; `Compact` after deleting 70% of them 0.09 s (48.7 → 13.7 MB).
   - **Where the time went:** in lookups, terms with punctuation filed under their first word run or first
     character (the 1,600 `e-` terms all under `e`, the 1,600 `.X` terms under `.`) were tried at every place with
     that character, 2,300 candidates per segment, and every probed beginning, though most are no key, cost a lookup
     in the immutable dictionary (2.6 million per pass over the document). In the check, every record was made anew
     for every segment, and its terms and translations classified again (a regular expression each). In loading,
     classifying the entries waited for the reading to end.
   - **Corrections to the generator:** the first one drew words from 30,000 pseudo-words of 36 syllables, too few for
     the termbase's single-word terms: every content word of the document was a term, and each of the 1,296
     two-syllable words was the beginning of about 20 others, so a segment had 33.7 hits (1.33 per word). And it
     measured adding the document's 50 most frequent words, which are function words (Zipf ranks 1–50) the
     generator never makes terms: 224 of 1,433 units re-checked on average, up to 1,333. Its frequent words are now
     the most frequent content words (ranks 100–149): 37 units on average, at most 95.
   - **Decision:** the format of section 3 and the index of the fifth row. Every budget is met, adding a term only if
     step 6 re-checks as the last row does:
     - **Loading** takes 1.7 s, on a background thread with "Loading…" (section 3).
     - **Adding a term:** step 6 finds the units to re-check by trying the new entry's own terms on each segment's
       `QaText` (2.8 ms for 5,000 segments; through a one-entry `TermIndex` 20 ms, so step 6 adds a public way to
       do that), and re-checks them with each segment's hits cached since its last check (section 6 caches them for
       the marks anyway) plus the new entry's. The commit (`synchronous=FULL`, p95 22 ms) runs in the write queue,
       so the UI waits only for the re-check, p95 about 36 ms. Re-checking the same units in full instead gives p95
       77 ms, or 47 ms with the units in parallel: the fallback if the cache isn't there.
     - **Re-checking the document** takes 0.48 s on one thread; step 5 may still run it off the UI thread (0.26 s
       in parallel), as it follows opening or loading the termbase.
     - **The index** is 189 MB (945 bytes per entry) as measured, 187 MB without the record field (above), near the
       budget: if a larger termbase needs it, notes leave the index first (the pane would read them from the file).
       The check's records are kept by its term source, not the index (21 MB for the document): step 5 makes a term
       source per document checked and drops it with the document, never one for the whole session, and after a
       write makes the new snapshot's source from the document's previous one.
   - **Also in this step:** the TM's schema check ignored any object named like an automatic index with `LIKE
     'sqlite_autoindex_%'`, where `_` matches any character, so a trigger named `sqliteXautoindexX…` passed unseen;
     it now ignores only an index whose name matches by `GLOB` and that has no SQL. In Core, a `TermRecord` copies
     its lists and keeps what the terminology check works out from it (`TermRecord.Checked`), and `Words.Fold` returns text that is folded
     already as it is.
4. **Exchange:** TBX 2 and 3 import (DCA and DCT), TBX 2 export (TBX 3 as an option), CSV import and export.
   - Sample files of every variant, from other tools where their licences allow; round trips through TBX keep
     entries, status (Unspecified included) and notes; CSV round trips keep what CSV can hold; damaged files fail
     only with the format exception; a DOCTYPE is skipped, never processed; legacy encodings, formula prefixes, a client term starting with `'`, and a term with `|` (reported).
   - The export checked by hand in at least one other tool, with the result recorded here.
5. **App: the termbase in the editor.** *(Done 2026-10-12.)* The Termbase menu, remembering and loading it, the terms
   list, the source marks, Ctrl+T, and the checks using it.
   - View-model tests with real `.sctb` files; headless tests for the list, the marks, the picker and inserting.
   - **Decisions:**
     - The menu has New…, Open… and Close; Import and Export come with step 4.
     - Opening loads in the background while the termbase already open stays in use, as a file or a TM that fails to
       open leaves the current one; a newer Open, Close or shutting down cancels the load, and a termbase that opened
       all the same is closed. A damaged remembered termbase is forgotten, a missing one kept, as for the TM.
     - A regional variant of the termbase's languages is served fully (a termbase has no scores to lower); a document
       without `trgLang` is judged by its source language, and setting it later decides again.
     - Each document gets its own term source (`DocumentTerms`), made with `ToTermSource(name, previous)` after each
       snapshot; `previous` only for a new snapshot of the same open termbase, never for another (even one with the
       same name, or the same file opened again), whose records the new index would never find. Its units are first checked with the file's glossary only; when the termbase serves the document
       (opened, loaded, or the languages now fit), every unit is checked again off the UI thread, in parallel, and a
       unit edited meanwhile keeps its own newer result. A QA report that has run is listed again, without switching
       tabs.
     - A termbase term that is also a term annotation gets the termbase's solid underline (its tooltip says what it
       is). Units not to be translated get no marks and no terms. A `Run` can't have a tooltip, so the source block's
       tooltip follows the term under the pointer.
     - The term picker sits where the code picker does (below the editor, not at the caret); both can move to the
       caret later together. A term replaces the selection (`Paste`'s rule), so a placeholder word can be selected and
       replaced.
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
