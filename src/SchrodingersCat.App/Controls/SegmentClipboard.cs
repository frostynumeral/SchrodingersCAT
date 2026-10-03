using Avalonia.Input;
using Avalonia.Input.Platform;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Controls;

/// <summary>
/// Copies segment content with its codes (design section 2, Paste). Other apps get plain text with <c>⟨1⟩</c>
/// placeholders. Alongside it goes a token in an app-specific format, which points at the copied content kept in
/// this process, so pasting inside the app restores the codes, and knows which unit they came from.
/// </summary>
/// <remarks>
/// A token rather than the content itself: the content and its unit stay objects, with nothing to serialize or
/// validate, and a token from another process (or an older copy) simply doesn't match, so the paste is plain text.
/// </remarks>
internal static class SegmentClipboard
{
    private static readonly DataFormat<string> TokenFormat = DataFormat.CreateStringApplicationFormat("SchrodingersCat.InlineContent");

    private static Copied? _last;

    public static async Task SetAsync(IClipboard clipboard, InlineContent content, TranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(unit);

        var token = Guid.NewGuid().ToString("N");
        _last = new Copied(token, content, new WeakReference<TranslationUnit>(unit));

        var item = new DataTransferItem();
        item.SetText(InlineFormatter.Format(content));
        item.Set(TokenFormat, token);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    /// <summary>
    /// What to paste: the copied content and its unit if it came from this app, else the clipboard's text (without
    /// chip characters, which would look like codes) and no unit. Null if there is nothing to paste.
    /// </summary>
    public static async Task<(InlineContent Content, TranslationUnit? Unit)?> GetAsync(IClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        var token = await clipboard.TryGetValueAsync(TokenFormat);
        if (token is not null && _last is { } last && last.Token == token && last.Unit.TryGetTarget(out var unit))
            return (last.Content, unit);

        var text = (await clipboard.TryGetTextAsync())?.Replace(TargetEditor.ChipCharacter.ToString(), "", StringComparison.Ordinal);
        return string.IsNullOrEmpty(text) ? null : (InlineContent.FromText(text), null);
    }

    // Weak, so a closed document isn't kept alive by the clipboard.
    private sealed record Copied(string Token, InlineContent Content, WeakReference<TranslationUnit> Unit);
}
