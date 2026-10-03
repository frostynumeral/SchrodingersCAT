using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// The code check results for one unit, shared by the unit's segment rows. Checks are per unit, because a pair can
/// span segments, so an edit in one segment can change the issues of another (design section 3).
/// </summary>
public sealed class UnitCheck
{
    public UnitCheck(TranslationUnit unit)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        Hints = new InlineCodeHints(unit);
        Issues = InlineCodeChecker.Check(unit);
    }

    public TranslationUnit Unit { get; }

    /// <summary>What the unit's source says about its codes. The source doesn't change, so neither do these.</summary>
    public InlineCodeHints Hints { get; }

    public IReadOnlyList<InlineCodeIssue> Issues { get; private set; }

    /// <summary>Raised after every <see cref="Run"/>.</summary>
    public event EventHandler? Checked;

    /// <summary>Checks the unit again; called after each edit (units are small).</summary>
    public void Run()
    {
        Issues = InlineCodeChecker.Check(Unit);
        Checked?.Invoke(this, EventArgs.Empty);
    }
}
