# Proposal: quality checks beyond codes

**Status:** accepted (2026-10-08). All [open questions](#open-questions) are decided as proposed; implementation
follows the [steps](#implementation-steps), starting with step 1.

## Problem

The editor checks inline codes (`InlineCodeChecker`, see [inline-code-editing.md](inline-code-editing.md)), and
nothing else. The mistakes that reach a client most often aren't about codes:

- a number changed or dropped (`Copy 3 files` → `3 fájl`, `12.5%` → `125%`);
- a segment left as its source, or left untranslated, in a file the translator thinks is done;
- a word the client fixed in the glossary translated another way;
- text marked `translate="no"` (a product name, a variable) translated anyway;
- a doubled word, a double space, a missing full stop, a space left at the end;
- the same sentence translated two ways in one file.

Every CAT tool has a QA check for these (memoQ's QA, Trados' Verify, Xbench). The translator runs it before
delivery and fixes or dismisses each warning. Without one, the translator proofreads for things a machine finds
better.

## What the checks have to do

- **Find** the problems above per segment while the translator works, and per document before delivery.
- **Show** them where the code issues already show: the segment's badge (count and worst severity), the tooltip,
  and Ctrl+Shift+Down to the next segment with something to look at.
- **Report** the whole document in one list, grouped by check, each entry jumping to its segment.
- **Let the translator dismiss** a warning that's wrong for this segment, without turning the check off everywhere.
- **Never block:** like code warnings, these are advice. Saving and Ctrl+Enter don't ask about them.

## Proposed design

Three layers, as before: checks in Core (no UI), issues in the app's existing badges, and a report in the pane.

### 1. Issues

Today a segment's issues are `InlineCodeIssue`s. The new checks report the same shape, so the app can treat both
alike:

```csharp
public sealed record QaIssue(QaIssueKind Kind, IssueSeverity Severity, Segment Segment, string Message)
{
    /// <summary>Where in the target (or the source, for something missing) the problem is, if it's one place.</summary>
    public TextSpan? Span { get; init; }
}
```

- `IssueSeverity` (Info, Warning, Error) is shared with the code check. No QA check reports an Error: an Error means
  the file breaks XLIFF's rules, which only codes can.
- `QaIssueKind` names the check, so the report can group by it, and a dismissal or a disabled check can name it.
- `Span` is element index plus offset into its text, and `SpanInSource` says whether it's in the source (for
  something missing). It's optional, and not shown in the first version (see [out of scope](#out-of-scope)), but
  checks that know where the problem is record it, so underlining can come later without changing the checks.
- The app wraps both kinds in one `SegmentIssue` view model (kind, severity, message, source), so badges, tooltips
  and navigation don't care which checker found an issue.

### 2. Segment checks

`QaChecker.Check(unit, options)` in `Core/Checks`, per unit like the code check, and for the same segments: those
translated and not ignorable. Each check compares one segment's target with its source; the text compared is the
plain text with each code as its `equiv` (standalone codes) or as U+FFFC (`QaText.CodeMark`), so a check never
looks inside native code and words on either side of a code don't run together. (Proposed as `equiv` or nothing,
but then `Line<br/>line` read as one word.)

| Check | Severity | Notes |
|---|---|---|
| **Numbers** differ | Warning | Each number in the source must appear in the target, and the target has no others. Compared by value, so `1,000.5` (en) and `1 000,5` (hu) are equal (see [open question 2](#open-questions)); a space separates groups only before three digits, and a number no reading fits (`1.2.3`) is compared as written. Numbers inside `translate="no"` spans are covered by that check instead; an extra number in the target is still reported, as the check can't know it was meant for the protected text. |
| **Target same as source** | Warning | Only when the source has letters, so `{0}`, `42` or `OK` copied over is fine if the source is that short (`OK` is skipped below 3 letters). |
| **`translate="no"` text changed** | Warning | The text inside each `<mrk translate="no">` (or `<sm>`/`<em>`) in the source must appear unchanged in the target. Doesn't need the annotation itself in the target, only its text. |
| **Terminology** | Warning | A source term from the file's glossary (below) is in the source, and none of its translations is in the target. |
| **Repeated word** | Warning | `the the`, case-insensitively, unless the source repeats it too. |
| **Spaces** | Info | Double spaces the source doesn't have; leading or trailing whitespace that differs from the source's. |
| **End punctuation** | Info | The source ends with `.`, `?`, `!` or `:` and the target with another (or none). A table maps equivalents (`。`, `？`, `！`, `：` for CJK, `;` for Greek `?`). |
| **Brackets and quotes** | Info | An opening bracket or quote without its closing one in the target, when the source has them balanced. |

Each check is a small class behind one interface (`IQaCheck`), so a check can be turned off (`QaOptions`), tested
alone, and added without touching the others.

**Words** are found and folded the way the concordance does it (lower case, accents removed, `fájl` finds `fájlt`).
That code lives in Memory today, which Core can't reference, so step 1 moves `MatchScore.Tokens` and
`Concordance.Fold` into Core (`Core/Text/Words`), and Memory uses them from there.

### 3. Terminology from the file

There's no termbase in the app, and building one is its own project. But files often carry their terms:

- the **glossary module** (`<gls:glossary>` in a unit), with `<gls:glossEntry>` entries holding a `<gls:term>` and
  zero or more `<gls:translation>`s, and a `ref` that may point to an annotation in the unit;
- **term annotations** (`<mrk type="term">`), which say a span is a term but carry no translation.

The check uses glossary entries that have a translation. If the entry points to an annotation (`ref="#m1"`; a
target annotation, `#t=m1`, doesn't count), the term is that annotation's text, and only the segment with it is
checked; otherwise every segment whose source contains the term
(by the folded word rule) is. The target passes if it contains any of the translations, by the same rule, so a
Hungarian suffix (`Tabulátort`) still counts. `GlossaryReader` (Core, like `XliffMatches` in Memory) reads the
entries from `ExtensionElements` and skips anything malformed, writing nothing back.

### 4. Document checks

Some problems only show across segments. `DocumentChecker.Check(document, options)` reports:

| Check | Severity | Notes |
|---|---|---|
| **Untranslated** | Warning | A translatable segment without a target. Normal while working, so only in the report, never on a badge. Content marked `translate="no"` at file, group or unit level is never reported. |
| **Inconsistent translation** | Info | The same source (compared as for an exact TM match) translated differently in the document. Each occurrence is listed with the others. |
| **Inconsistent source** | Info | The same target for different sources: sometimes right, often a copy-paste slip. Intentionally empty targets don't count. |

Each document issue is a `QaIssue` too, with `Related`: the segments it disagrees with. Sources and targets are
compared with the exact-match key (`ContentKey`: NFC, codes by kind), which moved from Memory into Core so the TM
and the checks share it.

### 5. In the editor

- **Badges:** `UnitCheck` runs the QA checker after the code checker, after every edit, and each `SegmentViewModel`
  exposes both kinds of issue (`Issues`, `HasWarnings`, `IssueSummary`). The badge counts both and shows the worst
  severity. QA issues don't outline chips (they aren't about codes).
- **Navigation:** Ctrl+Shift+Down goes to the next segment with errors or warnings, of either kind, as now.
- **The QA tab:** a third tab in the pane (Matches, Concordance, QA). F8 (memoQ's key) runs the document's checks
  and shows every issue, grouped by check, with the segment's label and the message. Clicking an entry, or Enter,
  selects the segment and gives the editor the keyboard. The list refreshes when F8 is pressed again, not on every
  edit; an issue fixed since shows as fixed (struck through) until then.
- **Dismissing:** an issue in the report or the badge's menu can be dismissed. It stops counting on the badge and
  in navigation, and shows greyed in the report. A dismissal names the check and the segment, and lasts as long as
  the target doesn't change (see [open question 4](#open-questions)).
- **Settings:** each check can be turned off in a QA settings dialog (QA tab → Settings…), saved in `AppSettings`.
  All are on by default.
- **Saving:** QA issues never prompt. Only code errors do (decision 2 of the inline code design).

## Out of scope

- Underlining the problem's place in the target (the issues carry a `Span` for it, later).
- A termbase of the translator's own, forbidden terms, and term extraction.
- Spelling and grammar.
- Length limits (the size and length restriction module, `slr:`).
- Checks across documents or against the TM (a TM entry translated differently from the document).
- Exporting the report (Xbench-style spreadsheets).

## Implementation steps

Each step ends with passing tests, zero warnings and green CI, and can be merged on its own.

1. **Core: segment checks.** *(Done 2026-10-08.)* `QaIssue`, `QaIssueKind`, `QaOptions`, `IQaCheck` and the segment
   checks but terminology (step 2); `Words` moved from Memory into Core (`Core/Text/Words`, internal; Memory sees it
   through `InternalsVisibleTo`).
   - Examples per check, including what each must not report (a source repeating a word, `OK` copied, numbers in
     another locale's format).
   - Properties over `XliffGen` documents: the checker never throws on any target; a target equal to its source
     reports only "same as source" (when the source has enough letters) and nothing about numbers, terms or
     `translate="no"`.
2. **Core: terminology and document checks.** *(Done 2026-10-08.)* `GlossaryReader`, the terminology check,
   `DocumentChecker`; the exact-match key (`ContentKey`) moved from Memory into Core with `Words`.
   - Examples with glossary entries with and without `ref`, several translations, a suffixed match; malformed
     glossaries skipped. Consistency with repetitions and near-repetitions.
3. **App: issues on badges.** `SegmentIssue`, `UnitCheck` running both checkers, badges, tooltips and
   Ctrl+Shift+Down covering both. View-model and headless tests.
4. **App: the QA tab.** F8, the grouped report, jumping to a segment, dismissing, the settings dialog. View-model and
   headless tests.
5. **Documentation:** CLAUDE.md, and mark this proposal implemented.

## Open questions

1. **Which checks, and how configurable?**
   - Option (a): the eight segment checks and three document checks above, all on, each switchable in a settings
     dialog (proposed).
   - Option (b): the same checks, all on, no settings yet (simpler; the first false alarm shows which need a switch).
   - Option (c): fewer checks to start (numbers, same as source, `translate="no"`, untranslated), the rest later.

   *Proposal:* (a). memoQ and Xbench users expect to switch checks off, and the settings are small.
   **Decision (2026-10-08):** (a), all checks, each switchable.
2. **Numbers in another format.**
   - Option (a): compare by value, accepting `.` and `,` as either decimal or group separator, and spaces (including
     no-break and narrow no-break) as group separators (proposed). `1.000` (de: one thousand) and `1.000` (en: one)
     are then equal, which is the rare case where it's wrong.
   - Option (b): parse with each language's rules (`CultureInfo` of `srcLang` and `trgLang`). Exact, but CLDR data
     differs between platforms and .NET's invariant-globalization mode.
   - Option (c): compare digit sequences only, ignoring separators: `12.5` and `125` are then equal, which is
     exactly the mistake to catch.

   *Proposal:* (a).
   **Decision (2026-10-08):** (a), by value, `.` and `,` either way.
3. **Terminology source.**
   - Option (a): the file's glossary module only (proposed). Nothing to build or store, and the client's terms are
     the ones that count.
   - Option (b): also a translator's termbase (a new store, import from TBX/CSV). Much more, and better as its own
     proposal.

   *Proposal:* (a).
   **Decision (2026-10-08):** (a), the file's glossary module only.
4. **Dismissals.**
   - Option (a): kept for the session while the target stays the same (proposed). Nothing is written to the file.
   - Option (b): kept in the file, e.g. as a `<note>` or in the validation module, so they survive reopening and reach
     a reviewer. Changes the file in ways other tools may show.
   - Option (c): kept in a sidecar file next to the document (`.xlf.qa.json`).

   *Proposal:* (a) first; (c) when translators ask for it.
   **Decision (2026-10-08):** (a), for the session while the target stays the same.
5. **Untranslated segments on badges?** Proposed: no, only in the report (F8), because while translating most of
   the file is untranslated, and a badge on every row means nothing.
   **Decision (2026-10-08):** only in the report.
