using CsCheck;
using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// <see cref="TargetEditModel"/> over documents from the shared generators (<c>SchrodingersCat.TestSupport</c>).
/// Replay a failure with <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class TargetEditModelPropertyTests
{
    [Fact]
    public void ToContent_RoundTripsEveryTargetExactly() =>
        XliffGen.Document.Sample(document =>
        {
            foreach (var unit in document.Units)
            {
                foreach (var segment in unit.Segments.Where(s => !s.IsIgnorable))
                    Assert.Equal(segment.Target ?? InlineContent.Empty, new TargetEditModel(unit, segment).ToContent());
            }
        });

    // Whatever the translator does, nothing throws, and a code that may not be deleted never disappears.
    [Fact]
    public void RandomEdits_NeverThrowAndNeverLoseProtectedCodes() =>
        Gen.Select(XliffGen.Document, Gen.Int[0, int.MaxValue].Array[0, 40]).Sample(sample =>
        {
            var (document, script) = sample;
            foreach (var unit in document.Units)
            {
                var hints = new InlineCodeHints(unit);
                foreach (var segment in unit.Segments.Where(s => !s.IsIgnorable))
                {
                    var model = new TargetEditModel(unit, segment);
                    var protectedAtStart = ProtectedHalves(model.ToContent(), hints);

                    foreach (var step in script)
                        Apply(model, segment, step);

                    Assert.InRange(model.Caret, 0, model.Length);
                    Assert.InRange(model.SelectionStart, 0, model.SelectionEnd);
                    Assert.Superset(protectedAtStart, ProtectedHalves(model.ToContent(), hints));
                    Assert.Equal(model.ToContent(), segment.Target ?? InlineContent.Empty);
                }
            }
        });

    private static void Apply(TargetEditModel model, Segment segment, int step)
    {
        var sourceIds = segment.Source.OfType<InlineCode>().Select(c => c.Id).Distinct().ToList();
        var position = step / 16 % (model.Length + 1);
        switch (step % 16)
        {
            case 0 or 1 or 2:
                model.InsertText(" é😀\t"[(step / 16) % 4].ToString());
                break;
            case 3:
                model.InsertText("szó ");
                break;
            case 4:
                model.MoveCaret(position);
                break;
            case 5:
                model.Select(position, step / 7 % (model.Length + 1));
                break;
            case 6 or 7:
                model.Delete(DeleteDirection.Backward);
                break;
            case 8:
                model.Delete(DeleteDirection.Forward);
                break;
            case 9:
                model.InsertNextMissingCode();
                break;
            case 10 when sourceIds.Count > 0:
                model.InsertCode(sourceIds[step / 16 % sourceIds.Count]);
                break;
            case 11:
                model.InsertCode("no-such-code");
                break;
            case 12:
                model.CopySource();
                break;
            case 13 or 14:
                model.Undo();
                break;
            default:
                model.Redo();
                break;
        }
    }

    private static HashSet<(string, InlineCodeKind)> ProtectedHalves(InlineContent content, InlineCodeHints hints) =>
        content.OfType<InlineCode>()
            .Where(c => hints.SourceCode(c.Id, c.Kind) is not null && hints.IsProtected(c.Id))
            .Select(c => (c.Id, c.Kind))
            .ToHashSet();
}
