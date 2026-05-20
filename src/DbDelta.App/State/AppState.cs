using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;
using DbDelta.Shared.Dtos;

namespace DbDelta.App.State;

/// <summary>
/// Shared application state for the Blazor Hybrid UI. Holds the current
/// connection strings, the last comparison result, and exposes an
/// <see cref="OnChange"/> notification that components subscribe to.
/// </summary>
public sealed class AppState
{
    public string SourceConnectionString { get; set; } = "";
    public string TargetConnectionString { get; set; } = "";
    public ComparisonResultDto? LastComparison { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy { get; private set; }

    public event Action? OnChange;

    public async Task RunComparisonAsync(CancellationToken ct)
    {
        IsBusy = true;
        LastError = null;
        OnChange?.Invoke();

        try
        {
            LiveDbSource src = new(SourceConnectionString, "source");
            LiveDbSource tgt = new(TargetConnectionString, "target");

            Result<Database> srcRes = await src.LoadAsync(ct);
            if (!srcRes.IsSuccess) { LastError = srcRes.Error!.Message; return; }

            Result<Database> tgtRes = await tgt.LoadAsync(ct);
            if (!tgtRes.IsSuccess) { LastError = tgtRes.Error!.Message; return; }

            ComparisonEngine engine = new();
            ComparisonResult result = engine.Compare(srcRes.Value!, tgtRes.Value!, ComparisonOptions.Default);
            LastComparison = Mapper.ToDto(result);
        }
        finally
        {
            IsBusy = false;
            OnChange?.Invoke();
        }
    }
}
