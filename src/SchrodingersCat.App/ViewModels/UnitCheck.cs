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
    private readonly Func<ITermSource?>? _terms;

    /// <param name="options">Which quality checks run, asked at every run; all if null.</param>
    /// <param name="translate">
    /// False for a unit marked <c>translate="no"</c> by its file or a group (<see cref="TranslationDocument.UnitsToTranslate"/>):
    /// its content is meant to stay, so the quality checks don't run on it.
    /// </param>
    /// <param name="terms">
    /// The termbase's term source for the terminology check (section 5 of <c>docs/design/termbase.md</c>), asked at
    /// every run; null, or returning null, for the file's glossary only.
    /// </param>
    public UnitCheck(TranslationUnit unit, Func<QaOptions>? options = null, bool translate = true, Func<ITermSource?>? terms = null)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _options = options ?? (() => QaOptions.Default);
        _translate = translate;
        _terms = terms;
        Hints = new InlineCodeHints(unit);
        CodeIssues = InlineCodeChecker.Check(unit);
        QaIssues = CheckQuality(_options(), _terms?.Invoke());
    }

    public TranslationUnit Unit { get; }

    /// <summary>What the unit's source says about its codes. The source doesn't change, so neither do these.</summary>
    public InlineCodeHints Hints { get; }

    public IReadOnlyList<InlineCodeIssue> CodeIssues { get; private set; }

    public IReadOnlyList<QaIssue> QaIssues { get; private set; }

    /// <summary>
    /// The unit is to be translated: neither it nor its file or a group is marked <c>translate="no"</c>. Only such
    /// units get quality checks, termbase marks and terms.
    /// </summary>
    public bool IsToTranslate => _translate && Unit.Translate != false;

    /// <summary>Counts the results: a re-check that ran in the background applies its result only if no edit came since.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// The quality checks' issues for the unit as it is now. It only reads the unit, so it may run on a background
    /// thread (the whole document's re-check after a termbase loads).
    /// </summary>
    internal IReadOnlyList<QaIssue> CheckQuality(QaOptions options, ITermSource? terms) =>
        _translate ? QaChecker.Check(Unit, options, terms) : [];

    /// <summary>Raised after every <see cref="Run"/> and <see cref="Apply"/>.</summary>
    public event EventHandler? Checked;

    /// <summary>Checks the unit again; called after each edit (units are small), and when the quality checks change.</summary>
    public void Run()
    {
        CodeIssues = InlineCodeChecker.Check(Unit);
        QaIssues = CheckQuality(_options(), _terms?.Invoke());
        Version++;
        Checked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes quality issues worked out by <see cref="CheckQuality"/> elsewhere (the code issues don't depend on the termbase).</summary>
    internal void Apply(IReadOnlyList<QaIssue> qaIssues)
    {
        QaIssues = qaIssues ?? throw new ArgumentNullException(nameof(qaIssues));
        Version++;
        Checked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-runs the quality checks of every unit with a new term source (section 5: opening, closing or loading the
    /// termbase), the units in parallel off the UI thread. Back on the caller's thread, a result is applied only if
    /// <paramref name="stillCurrent"/> says this re-check hasn't been superseded, and its unit hasn't been checked
    /// since (an edit checks it with the same term source, so its own result is newer).
    /// </summary>
    /// <param name="runOffUiThread">Runs the work in the background: <see cref="Task.Run(Action)"/> in the app, held at a gate in tests.</param>
    internal static async Task RecheckAsync(
        IReadOnlyList<UnitCheck> checks, QaOptions options, ITermSource? terms, Func<Action, Task> runOffUiThread, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runOffUiThread);
        ArgumentNullException.ThrowIfNull(stillCurrent);

        var versions = checks.Select(c => c.Version).ToArray();
        var results = new IReadOnlyList<QaIssue>[checks.Count];
        await runOffUiThread(() => Parallel.For(0, checks.Count, i => results[i] = checks[i].CheckQuality(options, terms)));
        if (!stillCurrent())
            return;

        for (var i = 0; i < checks.Count; i++)
        {
            if (checks[i].Version == versions[i])
                checks[i].Apply(results[i]);
        }
    }
}
