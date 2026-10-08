using System.Text;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Text;

/// <summary>
/// What makes two contents the same for an exact TM match, and for the QA checks' consistency: text in NFC, codes
/// as their kind only (so their ids don't count), annotations left out.
/// </summary>
internal static class ContentKey
{
    internal static string Of(InlineContent content)
    {
        var key = new StringBuilder();
        foreach (var element in content)
        {
            if (element is InlineText text)
                key.Append(Nfc(text.Text));
            else if (element is InlineCode code)
                key.Append('').Append((int)code.Kind);
        }

        return key.ToString();
    }

    /// <summary>NFC; content can hold code points Normalize rejects (a lone surrogate, U+FFFF, from <c>&lt;cp&gt;</c>), which stay as they are.</summary>
    internal static string Nfc(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
