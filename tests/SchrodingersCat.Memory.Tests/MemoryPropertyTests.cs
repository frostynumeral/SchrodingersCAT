using CsCheck;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// Matching over documents from the shared generators (<c>SchrodingersCat.TestSupport</c>).
/// Replay a failure with <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class MemoryPropertyTests
{
    private static readonly Gen<InlineContent[]> Sources =
        XliffGen.Document.Select(d => d.Units.SelectMany(u => u.Segments).Select(s => s.Source).ToArray());

    [Fact]
    public void Scores_AreSymmetricAndInRange_AndOnlyEqualSourcesScore100() =>
        Gen.Select(Sources, Sources).Sample((a, b) =>
        {
            foreach (var (x, y) in a.Zip(b))
            {
                var score = MatchScore.Compare(x, y);
                Assert.InRange(score, 0, 100);
                Assert.Equal(score, MatchScore.Compare(y, x));
                Assert.Equal(100, MatchScore.Compare(x, x));
                if (score == 100)
                    Assert.Equal(Strip(x), Strip(y));
            }
        });

    // Pairs that differ only in code ids are one entry (the newer one), so targets are compared as the TM does.
    [Fact]
    public void EveryStoredSegment_IsFoundAsAnExactMatch() =>
        XliffGen.Document.Sample(document =>
        {
            var tm = new InMemoryTranslationMemory();
            var translated = document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null).ToList();
            foreach (var segment in translated)
                tm.Add(new TmEntry(segment.Source, segment.Target!));

            foreach (var segment in translated)
                Assert.Contains(tm.Lookup(segment.Source, maxMatches: int.MaxValue), m => m.Score == 100 && Strip(m.Entry.Target) == Strip(segment.Target!));
        });

    // Transferring a segment's own entry onto its own source keeps every code the target has from that source, in
    // place, and leaves out only annotations and codes the source doesn't have.
    [Fact]
    public void TransferringOntoItsOwnSource_KeepsTheTargetsCodes() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var segment in document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null))
            {
                var sourceHalves = segment.Source.OfType<InlineCode>().Select(c => (c.Id, c.Kind)).ToHashSet();
                var used = new HashSet<(string, InlineCodeKind)>();
                var expected = new InlineContent(segment.Target!.Where(e => e switch
                {
                    InlineText => true,
                    InlineCode c => sourceHalves.Contains((c.Id, c.Kind)) && used.Add((c.Id, c.Kind)),
                    _ => false,
                }).Select(e => e is InlineCode c ? segment.Source.OfType<InlineCode>().First(s => s.Id == c.Id && s.Kind == c.Kind) : e));

                var result = CodeTransfer.Apply(new TmEntry(segment.Source, segment.Target!), segment.Source);

                Assert.Equal(expected, result.Target);
                Assert.Equal(used.SetEquals(sourceHalves) && segment.Target!.OfType<InlineCode>().Count() == used.Count, result.AllCodesPlaced);
            }
        });

    // What the score can't see: annotations, and code ids and attributes.
    private static string Strip(InlineContent content) => string.Concat(content.Select(e => e switch
    {
        InlineText t => Nfc(t.Text),
        InlineCode c => $"{(int)c.Kind}",
        _ => "",
    }));

    private static string Nfc(string text)
    {
        try
        {
            return text.Normalize();
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
