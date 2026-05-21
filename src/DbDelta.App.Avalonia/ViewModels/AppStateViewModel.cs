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
