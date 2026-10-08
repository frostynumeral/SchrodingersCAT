# Proposal: translation memory

**Status:** implemented (2026-10-07). All [open questions](#open-questions) were decided as proposed, and all [implementation steps](#implementation-steps) are done, as is the [concordance](#concordance-follow-up) follow-up; maintenance followed in [tm-maintenance.md](tm-maintenance.md).

## Problem

Translators reuse earlier translations. A translation memory (TM) stores translated segment pairs, and when the
translator moves to a segment, the editor looks up similar sources and offers their translations. Without one,
every repeated or near-repeated sentence is translated from scratch, inconsistently.

The editor has nothing of this yet. Files from clients often contain matches already (the XLIFF 2 matches module,
`<mtc:matches>`), which Core keeps verbatim in `TranslationUnit.ExtensionElements` but doesn't show.

## What a TM has to do

- **Store** segment pairs (source and target content, codes included) for one language pair, with when and where
  each was made.
- **Look up** an exact match (the same source, 100%), a context match (the same source and the same neighbouring
  segments, "101%"), and fuzzy matches (a similar source, scored 0–99%).
- **Show** the matches for the active segment, with what differs from the current source, and insert one with a key.
- **Learn:** confirming a segment (Ctrl+Enter) stores it.
- **Exchange:** import and export TMX 1.4b, the format every CAT tool reads and writes.

## Proposed design

Four layers, as for inline code editing, each testable on its own:

1. **Matching** (new project `SchrodingersCat.Memory`, no storage, no UI): entries, normalisation, scoring and code
   transfer, over an in-memory list.
2. **Storage** (same project): a TM file on disk, with an index that finds fuzzy candidates without scanning every
   entry.
3. **Exchange** (same project): TMX import and export, and reading `<mtc:matches>` from a unit.
4. **Integration** (`SchrodingersCat.App`): the matches pane, inserting matches, storing confirmed segments, and
   choosing the TM.

A separate project keeps Core's only dependency `Microsoft.Extensions.Logging.Abstractions`; the storage needs a
database package Core shouldn't carry.

### 1. Entries

```csharp
public sealed record TmEntry(
    InlineContent Source,
    InlineContent Target,
    string? PreviousSource,      // plain text of the neighbouring sources, for context matches
    string? NextSource,
    string? Document,            // the file it came from
    DateTimeOffset Changed,
    string? ChangedBy);
```

- **Content keeps codes.** Entries store `InlineContent`, so a match can bring its codes along. TMX represents them
  as `<bpt>`/`<ept>`/`<ph>`/`<it>`, which map onto the same runs.
- **One language pair per TM.** The TM knows its source and target language; entries don't repeat them. A
  document's languages must match the TM's (`de-AT` against a `de` TM is an [open question](#open-questions)).
- **Duplicates:** storing a source that's already there with a different target adds a second entry, the way
  memoQ and Trados do by default; storing the same pair again only updates `Changed`.

### 2. Matching

- **Normalisation** for comparing (never for storing): Unicode NFC, runs of whitespace as one space, and codes as
  one placeholder token each, so `Press ⟨b⟩OK⟨/b⟩` and `Press ⟨1⟩OK⟨/1⟩` compare equal.
- **Score:** word-level edit distance between the normalised sources, as a percentage of the longer one.
  Punctuation counts as words. Then penalties, each a few points:
  - codes that differ in number or kind;
  - numbers that differ (a match that differs only in numbers is still useful, so it's shown with the numbers
    marked, not dropped);
  - case or whitespace-only differences (99%, not 100%).
- **Exact and context:** 100% means equal normalised sources and equal codes. 101% adds equal neighbouring sources.
- **Threshold:** matches under 70% aren't shown (configurable later). At most 5 are shown, best first, newest first
  among equal scores.

### 3. Transferring codes

A match's target has the codes of the TM entry, not those of the current source. Inserting it has to map them:

- Pair the entry's source codes with the current source's codes in order, by kind (opening with opening, and so on).
- Replace each code in the match's target by its paired current code (same id, so the code check is satisfied).
- Codes that don't pair up are left out of the inserted text, and the match is marked so the translator knows to
  insert them (F9 does that).

The edit model applies the result through `Paste`, so the rules for protected and non-copyable codes hold as for
any other edit.

### 4. Storage

- **A SQLite file** per TM (`*.sctm`), through `Microsoft.Data.Sqlite`. One file is easy to copy and back up,
  survives crashes (transactions), and is fast enough for hundreds of thousands of entries.
- **Candidates for fuzzy matches** come from an index of word bigrams of the normalised source (with sentence start
  and end marks, numbers as one word): the entries sharing the most bigrams with the current source are scored, not
  the whole TM. A lookup reads the query's bigrams from the rarest up, within a budget of index rows, so very common
  bigrams can't make it slow. (Proposed as word trigrams, or SQLite's FTS5 with its trigram tokenizer; the step 2
  spike chose bigrams, see below.)
- **Content** is stored in a small binary format of its own (`TmContentFormat`), with every property of every
  element and strings as raw UTF-16. (Proposed as XLIFF inline XML, but Core has no content-only reader or writer,
  its document writer refuses what a TM entry legitimately holds, such as a pair half whose other half is in the next
  segment, and UTF-8 text or JSON would mangle the lone surrogates `<cp>` can bring in.)
- **Untrusted files:** a TM file can come from someone else, like an XLIFF file. Opening one checks its schema
  version and never runs anything stored in it.

### 5. Exchange

- **TMX 1.4b import/export:** `<tu>` with one `<tuv>` per language; codes as `<bpt>`/`<ept>`/`<ph>`/`<it>`. The
  importer has the same defences as the XLIFF reader (no DTDs, size and depth limits), and reports what it skipped
  (other language pairs, malformed units) instead of failing the whole import.
- **XLIFF matches module:** `<mtc:match>` elements in a unit are read into the same match type, shown alongside TM
  matches with their origin, and never modified (Core keeps writing them back verbatim).

### 6. In the editor

- **Matches pane** under the segment list: the active segment's matches with score, origin (TM name, file, date)
  and the source differences highlighted. Lookup runs in the background when the active segment changes, so
  moving between segments never waits on the TM.
- **Inserting:** Ctrl+1 … Ctrl+9 insert the nth match (memoQ's keys), replacing the target; the code transfer
  above applies. Undo restores the previous target.
- **Exact matches:** on entering an untranslated segment with a 100% or 101% match, the match is inserted
  automatically, and the segment stays Initial until confirmed (an [open question](#open-questions)).
- **Learning:** Ctrl+Enter stores the confirmed segment in the TM. Editing an already confirmed segment and
  confirming again updates its entry.
- **Choosing the TM:** a "Translation memory" menu to create, open and close a TM file. Which TM goes with which
  document is an [open question](#open-questions).

## Out of scope

- Server or shared TMs, and several translators writing one TM at once.
- Machine translation (the matches pane could show it later; it's a separate source).
- TM maintenance (editing or deleting entries), a natural follow-up now that the storage exists; done, see
  [TM maintenance](tm-maintenance.md). (Concordance
  search, the other one, is done: see [Concordance](#concordance-follow-up).)
- Subsegment matching (finding fragments of sentences).

## Implementation steps

Each step ends with passing tests, zero warnings and green CI, and can be merged on its own.

1. **Matching:** *(Done 2026-10-03.)* `TmEntry`, normalisation, scoring and code transfer over an in-memory TM.
   - Example tests per rule (whitespace, case, codes, numbers, context).
   - Property tests with the shared generators: a source matches itself at 100%; scores are within 0–101; inserting
     a 100% match of a unit's own segment reproduces its target codes exactly.
2. **Storage spike and SQLite TM:** *(Done 2026-10-03.)* trigram index versus FTS5 on a generated TM of 200,000
   entries; then the chosen one, with lookups under 50 ms at that size.
   - **Spike** (throwaway, not kept): 60,000 random sentences of 3–25 words from a 6,000-word Zipf vocabulary, plus
     140,000 variants with 1–4 word edits; 300 queries (100 stored sentences, 100 new variants, 100 new sentences);
     the in-memory TM's full scan as the reference.

     | Candidates from | Lookup, median / p95 | Same top 5 as the full scan | Matches found | Build |
     |---|---|---|---|---|
     | Full scan (reference) | 302 ms | 300/300 | 542/542 | |
     | FTS5 trigram, best 200 by rank | 743 / 1,276 ms | 291/300 | 526/542 | 4 s, 77 MB |
     | Word bigrams, best 200 | 47 / 55 ms | 296/300 | 541/542 | 9 s, 80 MB |
     | Word bigrams, best 100, budget 80,000 rows | 3.7 / 10.5 ms | 295/300 | 539/542 | |
     | Word bigrams, best 100, budget 20,000 rows | 2.6 / 4.8 ms | 291/300 | 524/542 | |

   - **Decision:** word bigrams, the 100 best candidates, a budget of 80,000 index rows per lookup. Almost all the
     time without a budget went into grouping the posting lists of very common bigrams, not into scoring.
3. **TMX import/export,** *(Done 2026-10-03.)* with the untrusted-input tests, and reading `<mtc:matches>`.
   - Codes keep their kind and pairing (and `x` ties source and target codes together), but not their native data:
     entries have no original data, and code transfer replaces a match's codes anyway.
   - A TMX unit the import can't use (no text in the language pair, an unknown element in `<seg>`, an `<ept>` without
     its `<bpt>`) is skipped and reported; a file that isn't safe, well-formed TMX fails as a whole.
4. **Matches pane and inserting matches** *(Done 2026-10-07.)* (Ctrl+1 … Ctrl+9), with headless tests.
   - The pane shows the file's matches (those whose `ref` points to the segment, or anywhere in a unit with only that
     segment to translate) and the open TM's, best first, at most nine; among equal scores the TM's come first.
   - The TM is looked up on a background thread, one lookup at a time (a SQLite connection can't be shared between
     threads); moving on cancels a lookup that hasn't started and drops the result of one that has. A damaged TM shows
     in the error banner, and the file's matches stay.
   - Context for 101% matches is the plain text of the neighbouring segments in the list.
   - The source differences (`SourceDiff`) are worked out on the words the score counts, but show what the score
     forgives: case and numbers. Whitespace is never a difference.
   - Inserting replaces the target through `TargetEditModel.ReplaceTarget`, with the rules of copying the source
     (codes already in another segment's target stay out, protected codes stay in), as one undoable edit.
   - Not in this step: opening a TM (step 5, so the app shows only the file's matches until then), and inserting
     exact matches automatically on entering a segment, which moves to step 5 together with storing.
5. **Learning and TM selection** *(Done 2026-10-07.)*: Ctrl+Enter stores, the Translation memory menu, auto-inserting
   exact matches, and the decisions below.
   - **Menu:** New…, Open…, Close, Import TMX…, Export TMX…. A new TM takes the open document's languages (asking for
     the target language if the file has none) and never replaces an existing file. The open TM is remembered in
     the user's settings (`settings.json`); at startup a damaged one is forgotten, a missing one only reported (it may
     be on a drive that isn't plugged in). Closing the window keeps it remembered; the menu's Close forgets it.
   - **Storing:** Ctrl+Enter stores the confirmed segment with its neighbours' plain text, the file name, the time and
     the user. A target with code errors is confirmed but not stored, and a notice says so. Re-confirming a corrected
     segment replaces the pair it stored before, or, for a segment already confirmed when the file was opened, its
     target then; only if that pair was stored from the same document, so the same translation from another file or
     an import stays (`ITranslationMemory.Replace`, one transaction). A file without `trgLang` takes the TM's target
     language when it first stores.
   - **One queue:** every TM operation runs in order on a background thread, so the store Ctrl+Enter queues is done
     before the next segment's lookup, and a repetition right below gets the translation.
   - **Auto-insert:** the best match, if it's a TM's 100% or 101%, goes into an empty target once per segment, and the
     segment stays Initial until confirmed. A file's 100% matches aren't inserted, as they may be machine translation.
     Redoing an undone automatic insertion counts as an edit (the segment becomes Translated).
   - **Languages:** a TM that doesn't serve the document (decision 3, `TmLanguages`) is neither looked up nor stored
     in, and the pane's header says why; a TM for the base language serves a regional variant with every score one
     point lower, so a 100% isn't inserted automatically.

## Concordance (follow-up)

*(Done 2026-10-07.)* Searching the TM for a word or phrase: how was it translated before?

- **Matching** (`Concordance`): the words `MatchScore` counts, without punctuation, folded (lower case, accents
  removed, and ł ø đ æ œ ß spelled out). A hit is a run of words, with only codes, annotations, punctuation and
  whitespace between them, each starting with the query's word at that place: `fájl` finds `fájlt`, `press ok` finds
  `Press <b>OK</b>.`. Whole original words are highlighted.
- **Order:** entries with a hit of whole words first, then the shorter text, then the newest, then the order stored;
  the best 100, and how many entries had a hit.
- **No index:** the word-bigram index of step 2 can't find single words, nor search translations, so the search
  scans every entry. The SQLite TM reads only the searched column, prefilters with the folded text, and loads only the
  kept hits; folding is per character, from a table, instead of normalizing every text. Measured on a 199,035-entry
  TM (186 MB, Release, 15 queries each):

  | Query | Sources, median / p95 | Translations, median / p95 |
  |---|---|---|
  | 1 common word (~24,000 hits) | 411 / 702 ms | 432 / 581 ms |
  | 1 rare word | 313 / 343 ms | 313 / 339 ms |
  | 1 word beginning | 303 / 331 ms | 321 / 410 ms |
  | 2 words | 315 / 501 ms | 323 / 888 ms |
  | 3 words | 320 / 342 ms | 313 / 451 ms |

  Under the 1 s set for p95, so no word index (which would need format version 2 and a migration). Normalizing every
  text instead of the per-character table cost about 200 ms more per search.
- **In the editor:** a Concordance tab beside Matches. Ctrl+K searches the selected text (the editor's: translations;
  a source's: sources), or opens the search box; Enter searches (never as you type), Escape returns to the editor.
  A search runs in the TM's queue, and a new search or another TM cancels it. Insert puts a result's translation at
  the caret as text (the selection stays; codes, which belong to another sentence, become their text or a space).
  Ctrl+1 … Ctrl+9 with the Concordance tab showing switch to the matches first.

## Open questions

1. **Which TM goes with a document?**
   - Option (a): one TM open at a time in the app, remembered between sessions (proposed: simplest, no "project").
   - Option (b): a TM per document, remembered next to it (e.g. a `.sctm` path in app settings keyed by file).
   - Option (c): introduce projects (documents plus TMs), as memoQ and Trados do.

   **Decision (2026-10-03):** (a), one TM open at a time, remembered between sessions.
2. **Auto-insert exact matches?** Proposed: yes for 101% and 100%, leaving the segment Initial so it still needs a
   confirmation. Some translators prefer to always choose.
   **Decision (2026-10-03):** yes, for 101% and 100%; the segment stays Initial until confirmed.
3. **Language variants:** should a `de` TM serve a `de-AT` document? Proposed: yes, with a 1-point penalty; a
   `de-AT` TM doesn't serve a `de-DE` document.
   **Decision (2026-10-03):** yes, with a 1-point penalty; a regional TM serves only its own region.
4. **Duplicates:** keep several translations of the same source (proposed), or keep only the newest?
   **Decision (2026-10-03):** keep several.
5. **Storage:** SQLite (proposed), or TMX files loaded into memory (no database dependency, but slow to open and
   save once a TM is large)?
   **Decision (2026-10-03):** SQLite.
6. **Match keys:** Ctrl+1 … Ctrl+9 (memoQ, proposed), or Ctrl+Up/Down through matches and Enter (Trados-like; but
   Ctrl+Up/Down already move between segments)?
   **Decision (2026-10-03):** Ctrl+1 … Ctrl+9.
