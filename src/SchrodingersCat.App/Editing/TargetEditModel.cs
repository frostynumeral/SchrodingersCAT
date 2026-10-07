using System.Globalization;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Editing;

/// <summary>
/// One segment's target while it is being edited: text and inline code chips, the caret and selection, the editing
/// operations with the XLIFF editing hints applied, and undo. Step 2 of <c>docs/design/inline-code-editing.md</c>.
/// </summary>
/// <remarks>
/// <para>Positions count UTF-16 characters, and each code or annotation half counts as one position, so the caret
/// can never land inside a chip. (The editor control will represent each chip as one private-use character, which
/// gives it the same positions.)</para>
/// <para>Every applied edit writes <see cref="Segment.Target"/> straight away, so dirty tracking and saving don't
/// change. The model never touches <see cref="Segment.State"/>; undo restores the content only (decision 8).</para>
/// <para>Which codes may be deleted or copied comes from <see cref="InlineCodeHints"/>, the same rules the
/// <see cref="InlineCodeChecker"/> applies.</para>
/// </remarks>
public sealed class TargetEditModel
{
    private readonly TranslationUnit _unit;
    private readonly Segment _segment;
    private readonly InlineCodeHints _hints;
    private readonly InlineContent? _emptyTarget;
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private List<Item> _items;
    private int _caret;
    private int _anchor;
    private EditKind _lastEdit;
    private int _lastEditCaret;

    public TargetEditModel(TranslationUnit unit, Segment segment)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        if (!unit.Segments.Contains(segment))
            throw new ArgumentException("The segment is not part of the unit.", nameof(segment));
        if (segment.IsIgnorable)
            throw new ArgumentException("Ignorables are left as extracted and can't be edited (decision 11).", nameof(segment));

        _hints = new InlineCodeHints(unit);

        // Clearing an intentionally empty target keeps it intentionally empty; clearing anything else means "not translated".
        _emptyTarget = segment.Target is { IsEmpty: true } ? InlineContent.Empty : null;
        _items = ToItems(segment.Target ?? InlineContent.Empty);
        _caret = _anchor = _items.Count;
    }

    /// <summary>The unit the segment belongs to.</summary>
    public TranslationUnit Unit => _unit;

    /// <summary>The segment whose target this is.</summary>
    public Segment Segment => _segment;

    /// <summary>What the unit's source says about its codes; the rules every edit follows.</summary>
    public InlineCodeHints Hints => _hints;

    /// <summary>Raised after every applied edit, undo and redo.</summary>
    public event EventHandler? Changed;

    /// <summary>The target as runs of text, code halves and annotation halves.</summary>
    public IReadOnlyList<EditToken> Tokens
    {
        get
        {
            var tokens = new List<EditToken>();
            var text = new System.Text.StringBuilder();
            foreach (var item in _items)
            {
                if (item.Element is null)
                {
                    text.Append(item.Char);
                    continue;
                }

                if (text.Length > 0)
                {
                    tokens.Add(new TextToken(text.ToString()));
                    text.Clear();
                }

                tokens.Add(item.Element is InlineCode code ? new CodeToken(code) : new MarkerToken((InlineMarker)item.Element));
            }

            if (text.Length > 0)
                tokens.Add(new TextToken(text.ToString()));
            return tokens;
        }
    }

    /// <summary>The number of positions: characters plus chips.</summary>
    public int Length => _items.Count;

    public int Caret => _caret;

    public int SelectionStart => Math.Min(_caret, _anchor);

    public int SelectionEnd => Math.Max(_caret, _anchor);

    public bool HasSelection => _caret != _anchor;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Moves the caret and removes the selection. Positions are clamped, and never split a surrogate pair.</summary>
    public void MoveCaret(int position) => Select(position, position);

    /// <summary>Selects from <paramref name="anchor"/> to <paramref name="caret"/> (either order).</summary>
    public void Select(int anchor, int caret)
    {
        _anchor = Snap(anchor);
        _caret = Snap(caret);
        _lastEdit = EditKind.None; // moving the caret ends a typing group
    }

    public InlineContent ToContent() => ToContent(_items);

    /// <summary>The selection as content (text, codes and annotation halves), for the clipboard.</summary>
    public InlineContent SelectedContent() => ToContent(_items.GetRange(SelectionStart, SelectionEnd - SelectionStart));

    /// <summary>Whether a half of the source code <paramref name="sourceCodeId"/> is missing from the unit's target.</summary>
    public bool IsMissing(string sourceCodeId)
    {
        ArgumentNullException.ThrowIfNull(sourceCodeId);

        var present = CodesInTarget();
        return _hints.SourceHalves(sourceCodeId).Any(h => !present.Contains((h.Id, h.Kind)));
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Types text at the caret, replacing the selection (protected codes in it are kept).</summary>
    public EditResult InsertText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 && !HasSelection)
            return EditResult.Unchanged;

        // Typing continues an undo step until a new word starts.
        var newWord = _items.Count > 0 && _caret > 0 && _items[_caret - 1].Element is null
            && char.IsWhiteSpace(_items[_caret - 1].Char) && text.Length > 0 && !char.IsWhiteSpace(text[0]);
        BeginEdit(EditKind.Typing, groupable: !HasSelection && !newWord);

        var kept = DeleteSelectionCore();
        _items.InsertRange(_caret, text.Select(c => new Item(c, null)));
        _caret = _anchor = _caret + text.Length;
        return Commit(KeptMessage(kept));
    }

    /// <summary>
    /// Inserts a source code. Halves that aren't in the target yet are inserted as they are; if the whole code is
    /// already there, a copy is inserted instead (refused for codes that may not be copied). A pair goes around the
    /// selection, or around the caret with the caret between its halves.
    /// </summary>
    public EditResult InsertCode(string sourceCodeId)
    {
        ArgumentNullException.ThrowIfNull(sourceCodeId);

        // Prefer the halves in this segment's source; a pair spanning segments has only one of them here.
        var halves = _segment.Source.OfType<InlineCode>().Where(c => c.Id == sourceCodeId).ToList();
        if (halves.Count == 0)
            halves = [.. _hints.SourceHalves(sourceCodeId)];
        if (halves.Count == 0)
            return EditResult.Refused($"There is no code '{sourceCodeId}' in the source.");

        var present = CodesInTarget();
        var missing = halves.Where(h => !present.Contains((h.Id, h.Kind))).ToList();
        if (missing.Count > 0)
            return InsertHalves(missing);

        if (!_hints.IsCopyable(sourceCodeId))
            return EditResult.Refused($"Code '{sourceCodeId}' is already in the target, and it may not be copied.");

        var copyId = NewId();
        return InsertHalves([.. halves.Select(h => Copy(h, copyId))]);
    }

    /// <summary>
    /// Pastes content at the caret, replacing the selection (protected codes in it are kept). Codes and annotations
    /// come along only from the same unit, following the same rules as <see cref="InsertCode"/>: a code missing from
    /// the target is inserted as it is (so cut and paste moves it), a code already there becomes a copy if it may be
    /// copied, and anything else is left out with a note. From another unit (or another app) only the text is pasted.
    /// </summary>
    /// <param name="from">The unit the content was copied from, or null if unknown.</param>
    public EditResult Paste(InlineContent content, TranslationUnit? from)
    {
        ArgumentNullException.ThrowIfNull(content);

        var notes = new List<string>();
        var pasted = ReferenceEquals(from, _unit) ? PastedItems(content, notes) : PastedText(content, notes);
        if (pasted.Count == 0 && !HasSelection)
            return notes.Count == 0 ? EditResult.Unchanged : EditResult.Refused(string.Join(" ", notes));

        BeginEdit(EditKind.Other, groupable: false);
        var kept = DeleteSelectionCore();
        _items.InsertRange(_caret, pasted);
        _caret = _anchor = _caret + pasted.Count;
        if (KeptMessage(kept) is { } keptNote)
            notes.Add(keptNote);
        return Commit(notes.Count == 0 ? null : string.Join(" ", notes));
    }

    /// <summary>Inserts the first code of this segment's source (in source order) that isn't in the target yet.</summary>
    public EditResult InsertNextMissingCode()
    {
        var present = CodesInTarget();
        var next = _segment.Source.OfType<InlineCode>().FirstOrDefault(c => !present.Contains((c.Id, c.Kind)));
        return next is null
            ? EditResult.Refused("All codes in the source are already in the target.")
            : InsertCode(next.Id);
    }

    /// <summary>
    /// Backspace or Delete: removes the selection (keeping protected codes), or one position next to the caret.
    /// A protected code is never deleted (decision 3); deleting one half of a pair leaves the other (decision 4).
    /// </summary>
    public EditResult Delete(DeleteDirection direction)
    {
        if (HasSelection)
        {
            if (_items.GetRange(SelectionStart, SelectionEnd - SelectionStart).All(IsProtected))
                return EditResult.Refused(ProtectedMessage(_items[SelectionStart]));

            BeginEdit(EditKind.Other, groupable: false);
            var kept = DeleteSelectionCore();
            return Commit(KeptMessage(kept));
        }

        var index = direction == DeleteDirection.Backward ? _caret - 1 : _caret;
        if (index < 0 || index >= _items.Count)
            return EditResult.Unchanged;

        var item = _items[index];
        if (IsProtected(item))
            return EditResult.Refused(ProtectedMessage(item));

        if (item.Element is not null)
        {
            BeginEdit(EditKind.Other, groupable: false); // a code edit is always its own undo step
            _items.RemoveAt(index);
        }
        else
        {
            var kind = direction == DeleteDirection.Backward ? EditKind.DeletingBackward : EditKind.DeletingForward;
            BeginEdit(kind, groupable: true);

            // Never leave half of a surrogate pair behind.
            var count = 1;
            if (direction == DeleteDirection.Backward && char.IsLowSurrogate(item.Char) && index > 0 && _items[index - 1] is { Element: null } high && char.IsHighSurrogate(high.Char))
            {
                index--;
                count = 2;
            }
            else if (direction == DeleteDirection.Forward && char.IsHighSurrogate(item.Char) && index + 1 < _items.Count && _items[index + 1] is { Element: null } low && char.IsLowSurrogate(low.Char))
            {
                count = 2;
            }

            _items.RemoveRange(index, count);
        }

        _caret = _anchor = index;
        return Commit(null);
    }

    /// <summary>
    /// Replaces the target with this segment's source (text, codes and annotations, with the same ids). Codes already
    /// used in another segment's target are left out (annotation halves too, silently), and protected codes the target
    /// had are kept at the end.
    /// </summary>
    public EditResult CopySource() => ReplaceTarget(_segment.Source);

    /// <summary>
    /// Replaces the target with <paramref name="content"/>, as one undoable edit: what inserting a TM match does (its
    /// codes already transferred onto this unit's). The rules of <see cref="CopySource"/> apply: code and annotation
    /// halves already in another segment's target are left out, and protected codes the target had are kept.
    /// </summary>
    public EditResult ReplaceTarget(InlineContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var elsewhere = OtherTargets().SelectMany(c => c).OfType<InlineCode>().Select(c => (c.Id, c.Kind)).ToHashSet();
        var markersElsewhere = OtherTargets().SelectMany(c => c).OfType<InlineMarker>().Select(m => (m.Id, m.Kind)).ToHashSet();
        var copied = new List<Item>();
        var skipped = new List<string>();
        foreach (var element in content)
        {
            if (element is InlineText text)
                copied.AddRange(text.Text.Select(c => new Item(c, null)));
            else if (element is InlineCode code && elsewhere.Contains((code.Id, code.Kind)))
                skipped.Add(code.Id);
            else if (element is InlineMarker marker && markersElsewhere.Contains((marker.Id, marker.Kind)))
                continue;
            else
                copied.Add(new Item('\0', element));
        }

        // Codes that may not be deleted stay, even if they came from another segment's source.
        var copiedCodes = copied.Select(i => i.Element).OfType<InlineCode>().Select(c => (c.Id, c.Kind)).ToHashSet();
        var keptProtected = _items.Where(i => IsProtected(i) && i.Element is InlineCode c && !copiedCodes.Contains((c.Id, c.Kind))).ToList();
        copied.AddRange(keptProtected);

        BeginEdit(EditKind.Other, groupable: false);
        _items = copied;
        _caret = _anchor = _items.Count;

        var notes = new List<string>();
        if (skipped.Count > 0)
            notes.Add($"Left out code(s) {string.Join(", ", skipped.Distinct().Select(i => $"'{i}'"))}, already used in another segment.");
        if (keptProtected.Count > 0)
            notes.Add(KeptMessage(keptProtected.Count)!);
        return Commit(notes.Count == 0 ? null : string.Join(" ", notes));
    }

    public EditResult Undo() => Restore(_undo, _redo);

    public EditResult Redo() => Restore(_redo, _undo);

    // ---------------------------------------------------------------- internals

    private enum EditKind
    {
        None,
        Typing,
        DeletingBackward,
        DeletingForward,
        Other,
    }

    // A character (Element null) or a code/annotation half.
    private readonly record struct Item(char Char, InlineElement? Element);

    private sealed record Snapshot(Item[] Items, int Caret, int Anchor);

    private void BeginEdit(EditKind kind, bool groupable)
    {
        var continues = groupable && kind == _lastEdit && _caret == _lastEditCaret && _undo.Count > 0;
        if (!continues)
            _undo.Push(new Snapshot([.. _items], _caret, _anchor));
        _redo.Clear();
        _lastEdit = kind;
    }

    private EditResult Commit(string? message)
    {
        _lastEditCaret = _caret;
        WriteTarget();
        return new EditResult(true, message);
    }

    private EditResult Restore(Stack<Snapshot> from, Stack<Snapshot> to)
    {
        if (from.Count == 0)
            return EditResult.Unchanged;

        to.Push(new Snapshot([.. _items], _caret, _anchor));
        var snapshot = from.Pop();
        _items = [.. snapshot.Items];
        _caret = snapshot.Caret;
        _anchor = snapshot.Anchor;
        _lastEdit = EditKind.None;
        WriteTarget();
        return EditResult.Done;
    }

    private void WriteTarget()
    {
        _segment.Target = _items.Count == 0 ? _emptyTarget : ToContent(_items);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Removes the selection, keeping protected codes (in their order) at its start; the caret goes before them.
    private int DeleteSelectionCore()
    {
        if (!HasSelection)
            return 0;

        var start = SelectionStart;
        var removed = _items.GetRange(start, SelectionEnd - start);
        var kept = removed.Where(IsProtected).ToList();
        _items.RemoveRange(start, removed.Count);
        _items.InsertRange(start, kept);
        _caret = _anchor = start;
        return kept.Count;
    }

    private EditResult InsertHalves(List<InlineCode> halves)
    {
        BeginEdit(EditKind.Other, groupable: false);
        if (halves is [{ Kind: InlineCodeKind.Opening } opening, { Kind: InlineCodeKind.Closing } closing])
        {
            var (start, end) = (SelectionStart, SelectionEnd);
            _items.Insert(end, new Item('\0', closing));
            _items.Insert(start, new Item('\0', opening));

            // Around the selection the caret goes after the pair; around the caret, between its halves.
            _caret = _anchor = start == end ? start + 1 : end + 2;
        }
        else
        {
            var at = SelectionEnd;
            _items.InsertRange(at, halves.Select(h => new Item('\0', h)));
            _caret = _anchor = at + halves.Count;
        }

        return Commit(null);
    }

    private static List<Item> PastedText(InlineContent content, List<string> notes)
    {
        if (content.Any(e => e is not InlineText))
            notes.Add("Codes from another unit were left out; only the text was pasted.");
        return [.. content.OfType<InlineText>().SelectMany(t => t.Text).Select(c => new Item(c, null))];
    }

    // Decides per code (all its halves together) and per annotation what comes along; see Paste.
    private List<Item> PastedItems(InlineContent content, List<string> notes)
    {
        // What the target will hold once the selection is replaced: the selection's protected codes stay.
        var remaining = _items.Take(SelectionStart).Concat(_items.Skip(SelectionEnd))
            .Concat(_items.GetRange(SelectionStart, SelectionEnd - SelectionStart).Where(IsProtected));
        var present = OtherTargets().SelectMany(c => c).Concat(remaining.Select(i => i.Element).OfType<InlineElement>())
            .Select(e => (IdOf(e), e is InlineCode code ? code.Kind : (InlineCodeKind?)null))
            .ToHashSet();
        var markerIds = present.Where(p => p.Item2 is null).Select(p => p.Item1).ToHashSet();

        var codes = content.OfType<InlineCode>().ToList();
        var markers = content.OfType<InlineMarker>().ToList();
        var replacements = new Dictionary<string, string?>(StringComparer.Ordinal); // pasted id → id to use, or null to leave out
        var leftOut = new List<string>();
        var copyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in codes.GroupBy(c => c.Id, StringComparer.Ordinal))
        {
            var halves = group.ToList();
            if (halves.All(h => _hints.SourceCode(h.Id, h.Kind) is not null && !present.Contains((h.Id, h.Kind))))
            {
                replacements[group.Key] = group.Key; // missing from the target: comes back as it is
                continue;
            }

            // Otherwise it becomes a copy of its source code: whole codes only, and only if that code may be copied.
            var baseId = BaseId(halves[0]);
            var whole = baseId is not null && _hints.SourceHalves(baseId).Count == halves.Count;
            if (baseId is null || !whole || !_hints.IsCopyable(baseId))
            {
                replacements[group.Key] = null;
                leftOut.Add(group.Key);
                continue;
            }

            var copyId = NewId(copyIds);
            copyIds.Add(copyId);
            replacements[group.Key] = copyId;
        }

        // An annotation comes along whole, and only if it isn't in the target yet (annotations aren't copied).
        var pastedMarkers = markers.GroupBy(m => m.Id, StringComparer.Ordinal)
            .Where(g => g.Count() == 2 && !markerIds.Contains(g.Key))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var items = new List<Item>();
        foreach (var element in content)
        {
            switch (element)
            {
                case InlineText text:
                    items.AddRange(text.Text.Select(c => new Item(c, null)));
                    break;
                case InlineCode code when replacements[code.Id] is { } id:
                    items.Add(new Item('\0', id == code.Id ? code : Copy(_hints.SourceCode(BaseId(code)!, code.Kind) ?? code, id)));
                    break;
                case InlineMarker marker when pastedMarkers.Contains(marker.Id):
                    items.Add(new Item('\0', marker));
                    break;
            }
        }

        if (leftOut.Count > 0)
            notes.Add($"Left out code(s) {string.Join(", ", leftOut.Select(i => $"'{i}'"))}: already in the target, and they may not be copied (or only part of the code was copied).");
        if (markers.Select(m => m.Id).Distinct(StringComparer.Ordinal).Any(id => !pastedMarkers.Contains(id)))
            notes.Add("Left out annotations that are already in the target or were only partly copied.");
        return items;
    }

    // The source code a pasted code stands for: itself, or the code it copies (by copyOf or a shared dataRef).
    private string? BaseId(InlineCode code)
    {
        if (_hints.IsInSource(code.Id) && _hints.SourceCode(code.Id, code.Kind) is not null)
            return code.Id;
        if (code.CopyOf is not null && _hints.IsInSource(code.CopyOf))
            return code.CopyOf;
        return code.DataRef is null ? null : _hints.SourceCodes.FirstOrDefault(c => c.Kind == code.Kind && c.DataRef == code.DataRef)?.Id;
    }

    // A copy has its own id, reuses the base code's original data, and names the base with copyOf only if it has none
    // (XLIFF 4.7.2.4.1).
    private static InlineCode Copy(InlineCode code, string id) => new(id, code.Kind)
    {
        DataRef = code.DataRef,
        CopyOf = code.DataRef is null ? code.Id : null,
        Type = code.Type,
        SubType = code.SubType,
        Disp = code.Disp,
        Equiv = code.Equiv,
        SubFlows = code.SubFlows,
        CanCopy = code.CanCopy,
        CanDelete = code.CanDelete,
        CanReorder = code.CanReorder,
        CanOverlap = code.CanOverlap,
        Dir = code.Dir,
        IsIsolated = code.IsIsolated,
        ExtensionAttributes = code.ExtensionAttributes,
    };

    // A source code (same id and kind) that must stay. Copies and unknown codes may always be deleted.
    private bool IsProtected(Item item) =>
        item.Element is InlineCode code && _hints.SourceCode(code.Id, code.Kind) is not null && _hints.IsProtected(code.Id);

    private static string ProtectedMessage(Item item) =>
        item.Element is InlineCode code
            ? $"Code '{code.Id}'{(code.Disp is null ? "" : $" ({code.Disp})")} may not be deleted."
            : "These codes may not be deleted.";

    private static string? KeptMessage(int kept) =>
        kept == 0 ? null : $"Kept {kept} code(s) that may not be deleted.";

    private IEnumerable<InlineContent> OtherTargets() =>
        _unit.Segments.Where(s => s != _segment && s.Target is not null).Select(s => s.Target!);

    // Code halves in the whole unit's target, with this segment's current content.
    private HashSet<(string Id, InlineCodeKind Kind)> CodesInTarget() =>
        OtherTargets().SelectMany(c => c).OfType<InlineCode>()
            .Concat(_items.Select(i => i.Element).OfType<InlineCode>())
            .Select(c => (c.Id, c.Kind))
            .ToHashSet();

    // Unused in the unit's source, target and segment ids, so a copy can't be mistaken for a source code.
    private string NewId(HashSet<string>? alsoTaken = null)
    {
        var used = _hints.SourceCodes.Select(c => c.Id)
            .Concat(_unit.Segments.Select(s => s.Id).OfType<string>())
            .Concat(_unit.Segments.SelectMany(s => s.Source.Concat(s.Target ?? InlineContent.Empty)).Select(IdOf).OfType<string>())
            .Concat(_items.Select(i => i.Element).Select(e => e is null ? null : IdOf(e)).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);

        for (var n = 1; ; n++)
        {
            var id = n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(id) && alsoTaken?.Contains(id) != true)
                return id;
        }
    }

    private static string? IdOf(InlineElement element) => element switch
    {
        InlineCode code => code.Id,
        InlineMarker marker => marker.Id,
        _ => null,
    };

    private int Snap(int position)
    {
        position = Math.Clamp(position, 0, _items.Count);
        if (position > 0 && position < _items.Count && _items[position - 1] is { Element: null } before && char.IsHighSurrogate(before.Char)
            && _items[position] is { Element: null } after && char.IsLowSurrogate(after.Char))
            position++;
        return position;
    }

    private static List<Item> ToItems(InlineContent content)
    {
        var items = new List<Item>();
        foreach (var element in content)
        {
            if (element is InlineText text)
                items.AddRange(text.Text.Select(c => new Item(c, null)));
            else
                items.Add(new Item('\0', element));
        }

        return items;
    }

    private static InlineContent ToContent(List<Item> items)
    {
        var elements = new List<InlineElement>();
        var text = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            if (item.Element is null)
            {
                text.Append(item.Char);
                continue;
            }

            if (text.Length > 0)
            {
                elements.Add(new InlineText(text.ToString()));
                text.Clear();
            }

            elements.Add(item.Element);
        }

        if (text.Length > 0)
            elements.Add(new InlineText(text.ToString()));
        return new InlineContent(elements);
    }
}
