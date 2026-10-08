using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Text;

namespace SchrodingersCat.Core.Checks;

/// <summary>
/// The checks that need the whole document (section 4 of <c>docs/design/qa-checks.md</c>): segments left
/// untranslated, and translations that disagree with each other. Issues come in document order.
/// </summary>
/// <remarks>
/// Sources and targets are compared as for an exact TM match: text in NFC, codes by kind (so code ids don't count),
/// annotations left out. Content marked <c>translate="no"</c> at file, group or unit level isn't expected to be
/// translated, so it's never reported as untranslated.
/// </remarks>
public static class DocumentChecker
{
    public static IReadOnlyList<QaIssue> Check(TranslationDocument document, QaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= QaOptions.Default;

        var segments = document.UnitsToTranslate.SelectMany(u => u.TranslatableSegments).ToList();
        var translated = segments.Where(s => s.Target is not null).ToList();
        var issues = new List<QaIssue>();

        if (options.IsEnabled(QaIssueKind.Untranslated))
        {
            issues.AddRange(segments.Where(s => s.Target is null)
                .Select(s => new QaIssue(QaIssueKind.Untranslated, IssueSeverity.Warning, s, "The segment isn't translated.")));
        }

        if (options.IsEnabled(QaIssueKind.InconsistentTranslation))
        {
            issues.AddRange(Inconsistent(translated, s => s.Source, s => s.Target!, QaIssueKind.InconsistentTranslation,
                (s, n) => $"“{Shorten(s.Source.ToPlainText())}” is translated {n} ways in this document."));
        }

        if (options.IsEnabled(QaIssueKind.InconsistentSource))
        {
            issues.AddRange(Inconsistent(translated.Where(s => !s.Target!.IsEmpty), s => s.Target!, s => s.Source, QaIssueKind.InconsistentSource,
                (s, n) => $"“{Shorten(s.Target!.ToPlainText())}” translates {n} different sources in this document."));
        }

        // In document order, whatever the check.
        var order = segments.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        return [.. issues.OrderBy(i => order[i.Segment])];
    }

    // Segments with the same key whose other side differs: each is reported, with the segments that disagree with it.
    private static IEnumerable<QaIssue> Inconsistent(
        IEnumerable<Segment> segments,
        Func<Segment, InlineContent> same,
        Func<Segment, InlineContent> other,
        QaIssueKind kind,
        Func<Segment, int, string> message)
    {
        foreach (var group in segments.GroupBy(s => ContentKey.Of(same(s)), StringComparer.Ordinal))
        {
            var variants = group.GroupBy(s => ContentKey.Of(other(s)), StringComparer.Ordinal).ToList();
            if (variants.Count < 2)
                continue;

            foreach (var segment in group)
            {
                var key = ContentKey.Of(other(segment));
                yield return new QaIssue(kind, IssueSeverity.Info, segment, message(segment, variants.Count))
                {
                    Related = [.. group.Where(s => ContentKey.Of(other(s)) != key)],
                };
            }
        }
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 59), "…");
}
