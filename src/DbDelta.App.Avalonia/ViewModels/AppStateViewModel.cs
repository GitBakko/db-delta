using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;
using DbDelta.Shared.Dtos;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Single-source-of-truth state for the comparison flow. Mirrors the old
/// <c>DbDelta.App.State.AppState</c> but uses CommunityToolkit MVVM
/// notifications + a <see cref="RelayCommand"/> for the Compare action.
/// </summary>
public sealed partial class AppStateViewModel : ObservableObject
{
    public AppStateViewModel(ConnectionStoreViewModel? connections = null)
    {
        Connections = connections;
        SourceSlot = new ConnectionPickerSlot(this, isSource: true);
        TargetSlot = new ConnectionPickerSlot(this, isSource: false);
    }

    public ConnectionStoreViewModel? Connections { get; }
    public ConnectionPickerSlot SourceSlot { get; }
    public ConnectionPickerSlot TargetSlot { get; }

    [ObservableProperty]
    private string _sourceConnectionString = "";

    [ObservableProperty]
    private string _targetConnectionString = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private ComparisonResultDto? _lastComparison;

    /// <summary>
    /// Active status filter for the result grid. <c>null</c> = no filter.
    /// Bound options match the raw <see cref="DifferenceDto.Status"/> string
    /// values ("Different" / "OnlyInA" / "OnlyInB" / "Identical").
    /// </summary>
    [ObservableProperty]
    private string? _statusFilter;

    /// <summary>
    /// Filter options shown in the picker — value is the raw Status string,
    /// label is the Italian display name.
    /// </summary>
    public IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new(null,         "Tutti"),
        new("Different",  "Modificato"),
        new("OnlyInA",    "Solo in origine"),
        new("OnlyInB",    "Solo in destinazione"),
        new("Identical",  "Identico"),
    ];

    /// <summary>
    /// Differences filtered by the current <see cref="StatusFilter"/>.
    /// Recomputed whenever LastComparison or StatusFilter change.
    /// </summary>
    public IReadOnlyList<DifferenceDto> FilteredDifferences =>
        LastComparison is null
            ? []
            : (StatusFilter is null
                ? LastComparison.Differences
                : [.. LastComparison.Differences.Where(d => string.Equals(d.Status, StatusFilter, StringComparison.Ordinal))]);

    partial void OnLastComparisonChanged(ComparisonResultDto? value) => OnPropertyChanged(nameof(FilteredDifferences));
    partial void OnStatusFilterChanged(string? value) => OnPropertyChanged(nameof(FilteredDifferences));

    public string StatusText => IsBusy ? "Working…" : "Ready";

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    [RelayCommand(CanExecute = nameof(CanCompare))]
    public async Task CompareAsync(CancellationToken ct)
    {
        IsBusy = true;
        LastError = null;
        try
        {
            // Trim defensively — pasted connection strings often carry a trailing
            // newline or stray whitespace from markdown / IDE clipboards that the
            // strict SqlConnectionStringBuilder parser rejects with the unhelpful
            // "Format of the initialization string does not conform to specification" error.
            string srcCs = (SourceConnectionString ?? string.Empty).Trim();
            string tgtCs = (TargetConnectionString ?? string.Empty).Trim();

            // Surface a sanitised echo of the parsed string when SqlClient throws
            // so we know exactly what the parser saw (e.g. a stray newline at
            // index N, or markdown ** that survived the paste).
            try
            {
                _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(srcCs);
            }
            catch (Exception parseEx)
            {
                LastError = $"Source connection string parse failed: {parseEx.Message}\n"
                    + $"len={srcCs.Length}\n"
                    + $"sanitised='{System.Text.RegularExpressions.Regex.Replace(srcCs, @"(?i)(password|pwd)\s*=\s*[^;]+", "$1=***")}'";
                return;
            }
            try
            {
                _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(tgtCs);
            }
            catch (Exception parseEx)
            {
                LastError = $"Target connection string parse failed: {parseEx.Message}\n"
                    + $"len={tgtCs.Length}\n"
                    + $"sanitised='{System.Text.RegularExpressions.Regex.Replace(tgtCs, @"(?i)(password|pwd)\s*=\s*[^;]+", "$1=***")}'";
                return;
            }

            LiveDbSource src = new(srcCs, "source");
            LiveDbSource tgt = new(tgtCs, "target");

            Result<Database> srcRes = await src.LoadAsync(ct).ConfigureAwait(true);
            if (!srcRes.IsSuccess)
            {
                LastError = srcRes.Error!.Message;
                return;
            }

            Result<Database> tgtRes = await tgt.LoadAsync(ct).ConfigureAwait(true);
            if (!tgtRes.IsSuccess)
            {
                LastError = tgtRes.Error!.Message;
                return;
            }

            ComparisonEngine engine = new();
            ComparisonResult result = engine.Compare(srcRes.Value!, tgtRes.Value!, ComparisonOptions.Default);
            LastComparison = Mapper.ToDto(result);

            if (Connections is not null)
            {
                await Connections.AutosaveAsync(srcCs, ct).ConfigureAwait(true);
                await Connections.AutosaveAsync(tgtCs, ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCompare() => !IsBusy
        && !string.IsNullOrWhiteSpace(SourceConnectionString)
        && !string.IsNullOrWhiteSpace(TargetConnectionString);

    [RelayCommand(CanExecute = nameof(CanSwap))]
    public void Swap() => (SourceConnectionString, TargetConnectionString) = (TargetConnectionString, SourceConnectionString);

    private bool CanSwap() => !IsBusy;

    partial void OnSourceConnectionStringChanged(string value) => CompareCommand.NotifyCanExecuteChanged();
    partial void OnTargetConnectionStringChanged(string value) => CompareCommand.NotifyCanExecuteChanged();
}
