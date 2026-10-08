# Proposal: translation memory maintenance

**Status:** accepted (2026-10-08). A follow-up to [translation memory](translation-memory.md), which left
maintenance out of scope "now that the storage exists". All [open questions](#open-questions) are decided as
proposed; implementation follows the [steps](#implementation-steps), starting with step 1.

## Problem

A TM collects mistakes: a typo confirmed in a hurry, a term the client has since changed, a whole file translated
against the wrong glossary, an imported TMX full of machine translation. Today the translator can't fix any of it.
Every bad entry keeps coming back as a match, and a 100% one is even inserted automatically (decision 2 of the TM
design). The only workarounds are re-confirming the segment in the same document (which replaces only that
document's own pair, `ITranslationMemory.Replace`) or exporting to TMX, editing it by hand and importing it into a
new TM.

## What maintenance has to do

- **Fix an entry where it shows up:** a bad match or concordance hit can be edited or deleted on the spot, without
  leaving the segment.
- **Browse the TM:** list its entries, filtered by text, document, user and date, newest first.
- **Delete in bulk:** the selected entries, or everything a filter finds (e.g. "everything from `old-client.xlf`"),
  after saying how many.
- **Keep the TM consistent:** the bigram index, the pair rules of `Add` (decision 4) and the queue's order hold after
  every change, and the file doesn't keep the space of what was deleted forever.

## Proposed design

Three layers again: the Memory project gains entry ids and the operations; the app gets actions on the pane's rows,
then a window for browsing and bulk work.

### 1. Entry ids

Today an entry is identified by its pair (normalised source and target, ignoring code ids), which `Add`, `Remove`
and `Replace` look up. That's not enough for maintenance: editing changes the pair, and a browser has to page
through 200,000 entries in a stable order.

```csharp
public readonly record struct TmEntryId(long Value);

public sealed record StoredEntry(TmEntryId Id, TmEntry Entry);
```

- **SQLite TM:** the id is the `entries` row id, which already exists. No format change: the file stays version 1.
  An id is never reused, so a stale id can't reach another entry: SQLite would hand out the highest id again once
  that entry is deleted, so the highest id deleted is kept in `meta` (a key-value table, so a new key needs no schema
  change), and a new entry gets the next id after both.
- **In-memory TM:** increasing ids from a counter, kept in the entry list (tests compare both TMs' results).
- **Where ids come from:** `TmMatch.EntryId` and `ConcordanceHit.EntryId` (null for matches from the XLIFF file,
  which aren't in a TM), and `StoredEntry` from browsing. `TmEntry` stays id-free, so what the app builds to store
  doesn't need one.
- **Stale ids:** an id can go stale in the queue (a re-confirmation replaced the pair, or another row's delete got
  there first). Then `Update` and `Delete` report it instead of throwing, and the app says "This entry is no longer
  in the TM" and refreshes.

### 2. Operations

Added to `ITranslationMemory`, implemented by both TMs, with the shared filter rules in the internal `Maintenance`
so they can't drift apart:

- `StoredEntry? Find(TmEntryId id)`. (Proposed as `Get`, which the analyzers reserve as a keyword of other languages.)
- `UpdateResult Update(TmEntryId id, InlineContent target, DateTimeOffset changed, string? changedBy)`: replaces an
  entry's translation and its `Changed` and `ChangedBy` (the app passes now and the user, as when storing).
  `Document` and the context stay: they say where the translation was made, and context matches (101%) still need
  them. If the new pair equals another stored pair (ignoring code ids), the two merge into one, as `Add` would
  (decision 4): the edited entry stays, with its id, and the other goes. The result says which: `Updated`, `Merged`
  or `NotFound`. Bigrams don't change, because the source doesn't (see [open question 2](#open-questions)).
- `int Delete(IReadOnlyCollection<TmEntryId> ids)`: removes the entries and their index rows, in one transaction.
  Returns how many were still there.
- `BrowseResult Browse(EntryFilter filter, TmEntryId? after, int pageSize)`: one page of matching entries, newest
  first (by id, which is the order stored), plus the total that match. Paging continues after the last id seen, not
  with an offset, so deleting rows doesn't make a page skip or repeat entries.
- `EntryFilter`: text (a `ConcordanceQuery`, on the source or the translation), document (exact file name), user,
  changed from (inclusive) and before (exclusive), and "sources with more than one translation" (sources compared
  as for an exact match). All optional; `EntryFilter.All` sets none, and a query without words sets no text
  condition.
- `int DeleteMatching(EntryFilter filter)`: what "Delete all N" does, in one transaction, with the same filter as
  the list the translator saw. It doesn't take a list of ids that could be 200,000 long.
- `IReadOnlyList<string> Documents()` and `IReadOnlyList<string> Users()`: the values the filter's lists offer.

**Filtering cost.** Only `changed` is a plain column. Document, user, source and target are `TmContentFormat` blobs.
A string is always stored as the same bytes, though, so SQLite compares document and user with the stored bytes of
the filter's value, and no row is decoded for them. Only the text and "several translations" conditions scan, and
they decode only the column they need, as the concordance does; whole entries are read only for the page shown.
"Several translations" needs every source's normalised key, which `MatchScore.Normalized.KeyOf` computes without
tokenizing. All filters stay well under the 1 s goal (step 1 below), so the format stays version 1.

**Bulk delete and the index.** `Delete` removes each entry's postings one by one (as `Remove` does now). That's
about 45 statements per entry, fine for dozens and slow for 50,000. When a delete removes 2% of the TM or more
(`BulkDeleteShare`, where the two cost the same, measured in step 1), it deletes the entries' postings in one pass
and recounts `features` from the postings that remain, in the same transaction. That needs no entry decoded.

**Space.** SQLite doesn't shrink a file when rows go. `Compact()` runs `VACUUM` once more than a quarter of the
entries have been deleted since the file was last compacted, counted in `meta`. (Proposed as more than a quarter of
the file's pages free, but scattered deletes leave pages half empty rather than free: after deleting every other
entry, `freelist_count` hardly moved.) The app calls it in the queue after a bulk delete, and the status says
"Compacting…". `VACUUM` keeps the schema exactly, so the file still passes `Open`'s checks, and it keeps the row ids
of a table with an integer primary key, so ids stay.

### 3. In the editor: actions on matches and concordance hits

The common case: a match that's wrong right now.

- **Delete:** each TM match and concordance row gets an Edit and a Delete button (non-focusable like the insert
  button, so the keyboard stays in the editor) and the same two items in a context menu. Ctrl+Shift+1 … Ctrl+Shift+9
  delete the nth match. Every delete asks first (`IDialogService.ConfirmAsync`, "Delete this entry from en-hu.sctm?").
  A file's own matches have neither action: nothing is ever written back to the XLIFF file's `<mtc:matches>`.
- **Edit:** opens `EntryEditorDialog`, with the entry's source read-only (chips, as in the source column) and its
  translation in a `TargetEditor`. The editor runs on a `TargetEditModel` over a one-segment `TranslationUnit` built
  from the entry, so typing, chips, F9, the code picker, the clipboard and undo all work as in the grid, with the
  same rules. Save is refused while `InlineCodeChecker` finds code errors, for the same reason a confirmed segment
  with code errors isn't stored. Escape or Cancel leaves the entry as it was.
- **After a change:** the selected segment's lookup runs again, so the pane shows what the TM now holds, and the
  concordance updates or drops the row. Both go through `OpenTranslationMemory`'s queue (`GetAsync`, `UpdateAsync`,
  `DeleteAsync`), so they're ordered with stores and lookups. Failures (`TmFormatException`, `DbException`,
  `IOException`) go to the error banner, as for the other TM operations.
- **The open document's segments:** editing an entry that one of its segments stored (same document name, source
  and translation as the segment's `StoredTarget`) moves that segment's `StoredTarget` to the new translation. Then
  re-confirming the segment replaces the corrected entry, instead of adding another translation beside it. Deleting
  such an entry clears `StoredTarget`.

### 4. The maintenance window

`Translation memory → Manage entries…` (enabled while a TM is open) opens `TmEntriesWindow`, a non-modal window
beside the editor:

- **Filters** across the top: the text box with Sources/Translations (the concordance's query rules), Document and
  User lists (`Documents()`, `Users()`), a date range, and "Several translations of the same source". The list
  refreshes when Enter is pressed or a list or date changes, never as you type, like the concordance.
- **List:** source and translation with chips and highlighted hits, plus document, date and user. It's virtualised
  and loads pages of 200 as you scroll (`Browse`), and the header shows "1,234 of 198,000 entries".
- **Actions:** Edit (double-click or Enter, the same `EntryEditorDialog`), Delete (the Delete key, for the selected
  rows), and "Delete all 1,234 matching…". Each delete confirms with the count. "Delete all" also suggests exporting
  to TMX first, because there's no undo (see [open question 3](#open-questions)).
- **Consistency with the editor:** the window and the main window share the one `OpenTranslationMemory` and its
  queue. A change in either refreshes the other's matches or list. Closing the TM, or opening another, closes the
  window.

## Out of scope

- Editing an entry's source (but see [open question 2](#open-questions)).
- Undo of a TM change after it's done.
- Search and replace across the TM (e.g. renaming a term in every translation). Edit and delete come first; this
  builds on `Browse` and `Update` later.
- Editing an entry's context, document, date or user by hand.
- Several translators maintaining one TM at once (shared TMs remain out of scope).
- Maintaining a TM without opening a document. The menu needs an open TM, not a document, so this works already.

## Implementation steps

Each step ends with passing tests, zero warnings and green CI, and can be merged on its own.

1. **Memory: ids and operations.** *(Done 2026-10-08.)* `TmEntryId`, `StoredEntry`, `EntryId` on matches and hits;
   `Find`, `Update`, `Delete`, `Browse`, `DeleteMatching`, `Documents`, `Users` and `Compact` on both TMs.
   - Examples per rule: merge on update, stale ids, ids never reused (across reopening too), paging across deletes,
     every filter, both delete paths, compacting.
   - Properties over `XliffGen` documents: a random script of adds, updates and deletes leaves the SQLite and
     in-memory TMs with equal entries and equal lookups; after any script, the SQLite index equals one rebuilt
     from the remaining entries (postings and feature counts), whichever delete path ran.
   - **Measured** (throwaway, not kept) on a generated TM like step 2's of the TM design: 60,000 random sentences of
     3–25 words from a 6,000-word Zipf vocabulary plus 140,000 variants with 1–4 word edits, 199,406 entries after
     merging pairs, 155 MB; 40 documents, 5 users, dates over a year. Release build, 15 runs each.

     | `Browse` | Median / p95 | Found |
     |---|---|---|
     | Every entry, first page or a middle one | 5 / 9 ms | 199,406 |
     | Dates (a month) | 45 / 51 ms | 16,570 |
     | Document | 27 / 28 ms | 4,986 |
     | User | 27 / 29 ms | 39,869 |
     | Text, the commonest word | 468 / 495 ms | 71,883 |
     | Text, a rare word | 181 / 186 ms | 160 |
     | Text in translations | 188 / 203 ms | 1,015 |
     | Several translations | 521 / 536 ms | |
     | Document and text | 35 / 36 ms | 632 |
     | `Documents()`, `Users()` | 37, 31 ms | |

     Before the document and user conditions went into SQL and the text scan read only its column, those filters
     took 690 ms and the commonest word 1,050 ms at p95; "several translations" took 2.2 s with the full
     normalisation instead of `KeyOf`.

     | Deleting every nth entry | One by one | At once | Then `Compact` |
     |---|---|---|---|
     | 0.1% (200) | 0.57 s | 0.66 s | |
     | 1% (1,995) | 0.76 s | 1.02 s | |
     | 5% (9,971) | 3.02 s | 1.31 s | |
     | 10% (19,941) | 5.36 s | 1.60 s | |
     | 25% (49,852) | 12.71 s | 1.98 s | 0.63 s, to 112 MB |
     | 50% (99,703) | 25.26 s | 2.60 s | 0.45 s, to 76 MB |
     | 90% (179,466) | 44.54 s | 3.54 s | 0.10 s, to 16 MB |

     One by one costs about 0.3 ms an entry, at once about 0.6 s plus a little, so `BulkDeleteShare` is 2%.
     `DeleteMatching` for one document (4,977 entries) takes 1.2 s, for one user (39,888) 2.0 s, for the commonest
     word (71,883) 3.0 s plus 0.5 s compacting. Deleting or editing one entry takes 5 / 8 ms and 3 / 4 ms.
2. **App: actions on rows.** The queue's new operations, Edit and Delete on TM matches and concordance hits,
   Ctrl+Shift+1 … 9, `EntryEditorDialog`, refreshing after changes, `StoredTarget` following edits. View-model tests
   with a fake TM, and headless tests: delete a match with confirmation, edit one (with a refused save on code
   errors), a stale id.
3. **App: the maintenance window.** `TmEntriesWindow` with filters, paging, multi-select delete, delete-all and
   compacting. Headless tests drive the window, and the main window's matches follow its changes.
4. **Documentation:** CLAUDE.md, and mark this proposal implemented.

## Open questions

1. **Where do edits start?**
   - Option (a): on the pane's rows and in a maintenance window (proposed). The rows cover "this match is wrong",
     and the window covers clean-ups.
   - Option (b): only in the window. It's less UI, but the translator has to search for an entry they're already
     looking at.
   - Option (c): only on the rows. Bulk clean-up would still need a TMX round trip.

   *Proposal:* (a), rows first (step 2), the window after (step 3).
   **Decision (2026-10-08):** (a), rows first, the window after.
2. **Edit the source too?** A typo in an imported source, or a source that changed slightly.
   - Option (a): translation only (proposed). The index stays as it is, and the entry still says what was
     translated.
   - Option (b): both. That needs re-indexing (delete and re-add the bigrams in the same transaction, which is cheap),
     and moves the entry's matches.

   *Proposal:* (a). memoQ and Trados allow (b); it can come later without changing the format.
   **Decision (2026-10-08):** (a), the translation only.
3. **Undo.**
   - Option (a): confirm every delete, no undo (proposed). Bulk deletes suggest a TMX export first.
   - Option (b): Undo for the last change while the window or segment is open, by re-adding what was deleted. It gets
     new ids and a new place in the stored order, which breaks ties in `Ranking`, so undoing isn't exact.
   - Option (c): no confirmation for a single row delete, with Undo in the status line instead.

   *Proposal:* (a). It's simple and honest. (c) is friendlier for the pane, and can be added later.
   **Decision (2026-10-08):** (a), confirm every delete, no undo.
4. **Compacting.**
   - Option (a): automatically, after a bulk delete leaves more than a quarter of the file free (proposed).
   - Option (b): a "Compact" menu item, run only when the translator asks.
   - Option (c): `auto_vacuum=INCREMENTAL`. That has to be set when the file is created, so it only helps new TMs,
     and only with a format change.

   *Proposal:* (a).
   **Decision (2026-10-08):** (a), automatically after a bulk delete.
5. **Edit details.** Should an edited entry keep its document and context (proposed: yes, they say where the
   translation was made, and 101% needs the context), or take the open document's?
   **Decision (2026-10-08):** keep the entry's document and context.
