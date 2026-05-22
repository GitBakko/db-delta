using System.Collections.Frozen;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

/// <summary>
/// View-model for the "New project" / project-setup dialog.
/// Owns both <see cref="Source"/> and <see cref="Target"/> endpoint panels and
/// exposes composite validity, swap, and build logic.
/// </summary>
public sealed partial class ProjectSetupViewModel : ObservableObject
{
    public ProjectSetupViewModel() : this(credentialStore: null) { }

    public ProjectSetupViewModel(ICredentialStore? credentialStore)
    {
        Source = new ProjectEndpointPanelViewModel("Source", isTarget: false, credentialStore);
        Target = new ProjectEndpointPanelViewModel("Target", isTarget: true, credentialStore);

        // Bubble endpoint validity changes up to this VM so bindings on IsValid
        // stay live.
        Source.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsValid));
        Target.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsValid));
    }

    // ── Endpoint panels ───────────────────────────────────────────────────────

    public ProjectEndpointPanelViewModel Source { get; }

    public ProjectEndpointPanelViewModel Target { get; }

    // ── Shared server scan ──────────────────────────────────────────────────

    /// <summary>True while a network-wide server scan is running. Mirrored into
    /// both panels' <c>IsScanningServers</c> so either spinner reflects the
    /// shared progress.</summary>
    [ObservableProperty] private bool _isScanningServers;

    /// <summary>
    /// Runs a single network-wide SQL Server scan and pushes the result into
    /// both source and target panels. The <paramref name="initiator"/>
    /// parameter ("source" / "target") is the panel whose "Scansiona" button
    /// was clicked; ONLY that panel's <c>IsScanningServers</c> flag is toggled
    /// so the spinner — and the deferred "open dropdown" UI hook — fires on
    /// the originating side alone. Both panels still receive the resulting
    /// <c>ServerSuggestions</c> via <c>ApplyScanResults</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScanServers))]
    public async Task ScanForAsync(string? initiator)
    {
        ProjectEndpointPanelViewModel? source = initiator == "target" ? null : Source;
        ProjectEndpointPanelViewModel? target = initiator == "source" ? null : Target;

        // Fallback: if no initiator was supplied, scan on behalf of both.
        if (source is null && target is null)
        {
            source = Source;
            target = Target;
        }

        IsScanningServers = true;
        BeginScan(source);
        BeginScan(target);

        try
        {
            IReadOnlyList<DiscoveredServer> list =
                await SqlServerDiscovery.EnumerateServersAsync(CancellationToken.None)
                                        .ConfigureAwait(true);
            // Both panels always get the populated list, regardless of initiator.
            Source.ApplyScanResults(list);
            Target.ApplyScanResults(list);
        }
        catch (Exception ex)
        {
            string msg = $"Errore: {ex.Message}";
            SetScanMessage(source, msg);
            SetScanMessage(target, msg);
        }
        finally
        {
            IsScanningServers = false;
            EndScan(source);
            EndScan(target);
        }

        static void BeginScan(ProjectEndpointPanelViewModel? p)
        {
            if (p is null) { return; }
            p.IsScanningServers = true;
            p.ScanStatusMessage = "Scansione in corso…";
        }

        static void EndScan(ProjectEndpointPanelViewModel? p)
        {
            if (p is null) { return; }
            p.IsScanningServers = false;
        }

        static void SetScanMessage(ProjectEndpointPanelViewModel? p, string message)
        {
            if (p is null) { return; }
            p.ScanStatusMessage = message;
        }
    }

    private bool CanScanServers() => !IsScanningServers;

    /// <summary>
    /// Seeds both panels' suggestion lists with the "Usati di recente" section
    /// derived from the connection store. Called once on dialog open before
    /// the auto-scan kicks in, so the picker is never empty.
    /// </summary>
    public void SeedRecentServers(IReadOnlyList<(string Name, string? IpAddress)> recents)
    {
        Source.SeedRecentServers(recents);
        Target.SeedRecentServers(recents);
    }

    /// <summary>
    /// Copies the source-panel connection fields into the target panel so the
    /// user can run a same-server compare with one click. Triggered by the
    /// "Clona configurazione" footer button.
    /// </summary>
    [RelayCommand]
    private void CloneSourceToTarget()
    {
        Target.ServerName = Source.ServerName;
        Target.AuthMode = Source.AuthMode;
        Target.UserName = Source.UserName;
        Target.Password = Source.Password;
        Target.RememberCredentials = Source.RememberCredentials;
        Target.Encrypt = Source.Encrypt;
        Target.TrustServerCertificate = Source.TrustServerCertificate;
        Target.DatabaseName = Source.DatabaseName;
        Target.ServerIpAddress = Source.ServerIpAddress;
        // Mirror the suggestions list so the Target combo isn't empty.
        Target.ServerSuggestions.Clear();
        foreach (DiscoveredServer s in Source.ServerSuggestions)
        {
            Target.ServerSuggestions.Add(s);
        }
        Target.HasServerSuggestions = Source.HasServerSuggestions;
        Target.AvailableDatabases.Clear();
        foreach (string db in Source.AvailableDatabases)
        {
            Target.AvailableDatabases.Add(db);
        }
        Target.HasDatabases = Source.HasDatabases;
        Target.ServerVersion = Source.ServerVersion;
        Target.ServerMajorVersion = Source.ServerMajorVersion;
    }

    // ── Project-level fields ─────────────────────────────────────────────────

    [ObservableProperty] private string _projectName = "Nuovo progetto";

    [ObservableProperty]
    private ProjectOptions _options = new(false, false, true, false, false);

    [ObservableProperty]
    private ObservableCollection<OwnerMappingEntry> _ownerMappings = [];

    [ObservableProperty]
    private ObservableCollection<TableMappingEntry> _tableMappings = [];

    partial void OnProjectNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    // ── Validity ──────────────────────────────────────────────────────────────

    public bool IsValid => Source.IsValid && Target.IsValid;

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SwapEndpoints()
    {
        // Exchange all non-identity fields field-by-field.
        (string srcServer, string tgtServer) = (Source.ServerName, Target.ServerName);
        (AuthenticationMode srcMode, AuthenticationMode tgtMode) = (Source.AuthMode, Target.AuthMode);
        (string srcUser, string tgtUser) = (Source.UserName, Target.UserName);
        (string srcPwd, string tgtPwd) = (Source.Password, Target.Password);
        (bool srcRemember, bool tgtRemember) = (Source.RememberCredentials, Target.RememberCredentials);
        (bool srcEncrypt, bool tgtEncrypt) = (Source.Encrypt, Target.Encrypt);
        (bool srcTrust, bool tgtTrust) = (Source.TrustServerCertificate, Target.TrustServerCertificate);
        (string srcDb, string tgtDb) = (Source.DatabaseName, Target.DatabaseName);

        Source.ServerName = tgtServer;
        Source.AuthMode = tgtMode;
        Source.UserName = tgtUser;
        Source.Password = tgtPwd;
        Source.RememberCredentials = tgtRemember;
        Source.Encrypt = tgtEncrypt;
        Source.TrustServerCertificate = tgtTrust;
        Source.DatabaseName = tgtDb;

        Target.ServerName = srcServer;
        Target.AuthMode = srcMode;
        Target.UserName = srcUser;
        Target.Password = srcPwd;
        Target.RememberCredentials = srcRemember;
        Target.Encrypt = srcEncrypt;
        Target.TrustServerCertificate = srcTrust;
        Target.DatabaseName = srcDb;
    }

    // ── Build / FromProject / LoadFrom ───────────────────────────────────────

    /// <summary>
    /// Materialises a <see cref="DbDeltaProject"/> from the current VM state.
    /// </summary>
    public DbDeltaProject Build() =>
        new(ProjectName,
            CreatedUtc: DateTime.UtcNow,
            LastModifiedUtc: DateTime.UtcNow,
            Source: Source.ToEndpoint(),
            Target: Target.ToEndpoint(),
            OwnerMappings: [.. OwnerMappings],
            TableMappings: [.. TableMappings],
            ProjectOptions: Options,
            Selections: FrozenDictionary<ObjectSelectionKey, bool>.Empty);

    /// <summary>
    /// Loads project data into the current VM instance in-place.
    /// Used by the "Carica…" button so the dialog stays open for review/edit.
    /// </summary>
    public void LoadFrom(DbDeltaProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectName = project.Name;
        Options = project.ProjectOptions;

        OwnerMappings.Clear();
        foreach (OwnerMappingEntry entry in project.OwnerMappings)
        {
            OwnerMappings.Add(entry);
        }

        TableMappings.Clear();
        foreach (TableMappingEntry entry in project.TableMappings)
        {
            TableMappings.Add(entry);
        }

        Source.LoadFromEndpoint(project.Source);
        Target.LoadFromEndpoint(project.Target);
    }

    /// <summary>
    /// Constructs a <see cref="ProjectSetupViewModel"/> pre-populated from an
    /// existing project.  Passing <see langword="null"/> returns a blank setup.
    /// </summary>
    public static ProjectSetupViewModel FromProject(
        DbDeltaProject? p,
        ICredentialStore? credentialStore = null)
    {
        ProjectSetupViewModel vm = new(credentialStore);
        if (p is null)
        {
            return vm;
        }

        vm.ProjectName = p.Name;
        vm.Options = p.ProjectOptions;

        vm.OwnerMappings.Clear();
        foreach (OwnerMappingEntry entry in p.OwnerMappings)
        {
            vm.OwnerMappings.Add(entry);
        }

        vm.TableMappings.Clear();
        foreach (TableMappingEntry entry in p.TableMappings)
        {
            vm.TableMappings.Add(entry);
        }

        // Repopulate endpoint panels from the saved endpoints.
        var src =
            ProjectEndpointPanelViewModel.FromEndpoint(p.Source, "Source", isTarget: false, credentialStore);
        var tgt =
            ProjectEndpointPanelViewModel.FromEndpoint(p.Target, "Target", isTarget: true, credentialStore);

        // Copy fields into the pre-wired panels so PropertyChanged subscriptions
        // remain intact.
        CopyEndpoint(src, vm.Source);
        CopyEndpoint(tgt, vm.Target);

        return vm;
    }

    /// <summary>
    /// Materialises the source connection string using the live VM password
    /// (which is never serialised in <see cref="DbDeltaProject"/>). Used by
    /// <c>App.OnFrameworkInitializationCompleted</c> to seed <c>AppState</c>
    /// without re-asking the user for credentials.
    /// </summary>
    public string BuildSourceConnectionString() => BuildConnectionString(Source);

    /// <summary>
    /// Materialises the target connection string using the live VM password.
    /// </summary>
    public string BuildTargetConnectionString() => BuildConnectionString(Target);

    private static string BuildConnectionString(ProjectEndpointPanelViewModel p)
    {
        string baseCs = $"Server={p.ServerName};Database={p.DatabaseName};"
            + $"Encrypt={p.Encrypt};TrustServerCertificate={p.TrustServerCertificate}";
        return p.AuthMode switch
        {
            AuthenticationMode.WindowsIntegrated => $"{baseCs};Integrated Security=True",
            AuthenticationMode.SqlServer => $"{baseCs};User Id={p.UserName};Password={p.Password}",
            _ => $"{baseCs};User Id={p.UserName};Password={p.Password}",
        };
    }

    private static void CopyEndpoint(
        ProjectEndpointPanelViewModel from,
        ProjectEndpointPanelViewModel to)
    {
        to.ServerName = from.ServerName;
        to.AuthMode = from.AuthMode;
        to.UserName = from.UserName;
        to.Password = from.Password;
        to.RememberCredentials = from.RememberCredentials;
        to.Encrypt = from.Encrypt;
        to.TrustServerCertificate = from.TrustServerCertificate;
        to.DatabaseName = from.DatabaseName;
    }
}
