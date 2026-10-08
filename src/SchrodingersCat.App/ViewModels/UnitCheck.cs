using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// The check results for one unit, shared by the unit's segment rows: the code check (design section 3 of the inline
/// code editing design) and the quality checks (<c>docs/design/qa-checks.md</c>). Checks are per unit, because a pair
/// can span segments, so an edit in one segment can change the issues of another.
/// </summary>
public sealed class UnitCheck
{
    private readonly Func<QaOptions> _options;
    private readonly bool _translate;

    /// <param name="options">Which quality checks run, asked at every run; all if null.</param>
    /// <param name="translate">
    /// False for a unit marked <c>translate="no"</c> by its file or a group (<see cref="TranslationDocument.UnitsToTranslate"/>):
    /// its content is meant to stay, so the quality checks don't run on it.
    /// </param>
    public UnitCheck(TranslationUnit unit, Func<QaOptions>? options = null, bool translate = true)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _options = options ?? (() => QaOptions.Default);
        _translate = translate;
        Hints = new InlineCodeHints(unit);
        CodeIssues = InlineCodeChecker.Check(unit);
        QaIssues = CheckQuality();
    }

    public TranslationUnit Unit { get; }

    /// <summary>What the unit's source says about its codes. The source doesn't change, so neither do these.</summary>
    public InlineCodeHints Hints { get; }

    public IReadOnlyList<InlineCodeIssue> CodeIssues { get; private set; }

    public IReadOnlyList<QaIssue> QaIssues { get; private set; }

    private IReadOnlyList<QaIssue> CheckQuality() => _translate ? QaChecker.Check(Unit, _options()) : [];

    /// <summary>Raised after every <see cref="Run"/>.</summary>
    public event EventHandler? Checked;

    /// <summary>Checks the unit again; called after each edit (units are small), and when the quality checks change.</summary>
    public void Run()
    {
        CodeIssues = InlineCodeChecker.Check(Unit);
        QaIssues = CheckQuality();
        Checked?.Invoke(this, EventArgs.Empty);
    }
}
