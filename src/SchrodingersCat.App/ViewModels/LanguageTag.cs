using System.Text.RegularExpressions;

namespace SchrodingersCat.App.ViewModels;

/// <summary>Checks a target language the translator types in (XLIFF's <c>trgLang</c>, an <c>xs:language</c>).</summary>
public static partial class LanguageTag
{
    /// <summary>Why <paramref name="tag"/> can't be the target language, or null if it can (surrounding spaces are ignored).</summary>
    public static string? Problem(string tag, string sourceLanguage)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(sourceLanguage);

        tag = tag.Trim();
        if (tag.Length == 0)
            return "Enter a language code, such as hu or de-AT.";
        if (!XsLanguage().IsMatch(tag))
            return $"'{tag}' isn't a language code. Use letters, digits and hyphens, such as hu or de-AT.";
        if (string.Equals(tag, sourceLanguage, StringComparison.OrdinalIgnoreCase))
            return $"That's the source language ({sourceLanguage}).";
        return null;
    }

    // The pattern of xs:language.
    [GeneratedRegex("^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$")]
    private static partial Regex XsLanguage();
}
