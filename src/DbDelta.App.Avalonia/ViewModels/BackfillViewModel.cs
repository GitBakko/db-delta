using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.App.ViewModels;

/// <summary>
/// One row of <see cref="Views.BackfillDialog"/>: a column the run cannot add
/// without a value for the rows already in the table, plus the value the
/// operator chose for it.
/// </summary>
public sealed partial class BackfillRowViewModel : ObservableObject
{
    /// <summary>
    /// Seeds the editable value with <see cref="BackfillRequirement.SuggestedValue"/>
    /// — a starting point of the right shape, not a decision.
    /// </summary>
    public BackfillRowViewModel(BackfillRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        Requirement = requirement;
        _value = requirement.SuggestedValue;
    }

    /// <summary>The column the script would fail on, as the preflight found it.</summary>
    public BackfillRequirement Requirement { get; }

    /// <summary>Schema-qualified table gaining the column.</summary>
    public string QualifiedTable => $"{Requirement.Schema}.{Requirement.Table}";

    /// <summary>The new column's name.</summary>
    public string ColumnName => Requirement.Column;

    /// <summary>Its declared type, so the operator can see what fits.</summary>
    public string DataType => Requirement.DataType;

    /// <summary>
    /// The DEFAULT expression to seed the existing rows with, verbatim — it is
    /// emitted straight after <c>DEFAULT</c>, so it carries its own parentheses
    /// and quoting exactly like a T-SQL default does.
    /// </summary>
    [ObservableProperty]
    private string _value;
}

/// <summary>
/// Data context for <see cref="Views.BackfillDialog"/>. Raised before a single
/// line of SQL runs, when <see cref="BackfillPreflight"/> found columns that
/// would be added as NOT NULL with nothing to put in the rows that already
/// exist (Msg 4901). The dialog's job is to turn that into an answer: one row
/// per column, a suggested value of the right shape, and the operator's edit.
/// </summary>
public sealed partial class BackfillViewModel : ObservableObject
{
    /// <summary>
    /// Builds one row per requirement, in the order the preflight returned them.
    /// </summary>
    public BackfillViewModel(IReadOnlyList<BackfillRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        foreach (BackfillRequirement requirement in requirements)
        {
            BackfillRowViewModel row = new(requirement);
            // Confirmation is gated on every row having a value, so the gate has
            // to re-evaluate while the user types — not only when rows come and
            // go, which they never do here.
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanConfirm));
            Rows.Add(row);
        }
    }

    /// <summary>The columns awaiting a value, one row each.</summary>
    public ObservableCollection<BackfillRowViewModel> Rows { get; } = [];

    /// <summary>
    /// How many columns the run cannot add unaided — the headline of the dialog.
    /// </summary>
    public int ColumnCount => Rows.Count;

    /// <summary>
    /// False while any row is blank. A blank is not "no value": leaving it out
    /// of the map emits the column unchanged and the deploy dies on that table,
    /// halfway through, which is precisely what this dialog exists to prevent.
    /// </summary>
    public bool CanConfirm => Rows.All(r => !string.IsNullOrWhiteSpace(r.Value));

    /// <summary>
    /// The map <see cref="ScriptGenerator.Generate"/> looks the
    /// values up by.
    /// </summary>
    public IReadOnlyDictionary<(string Schema, string Table, string Column), string> ToMap() =>
        Rows.ToDictionary(r => r.Requirement.Key, r => r.Value.Trim());
}
