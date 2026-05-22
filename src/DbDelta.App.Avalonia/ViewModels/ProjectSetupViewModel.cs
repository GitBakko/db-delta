using System.Collections.Frozen;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;

namespace DbDelta.App.ViewModels;

/// <summary>
/// View-model for the "New project" / project-setup dialog.
/// Owns both <see cref="Source"/> and <see cref="Target"/> endpoint panels and
/// exposes composite validity, swap, and build logic.
/// </summary>
public sealed partial class ProjectSetupViewModel : ObservableObject
{
    public ProjectSetupViewModel()
    {
        // Bubble endpoint validity changes up to this VM so bindings on IsValid
        // stay live.
        Source.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsValid));
        Target.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsValid));
    }

    // ── Endpoint panels ───────────────────────────────────────────────────────

    public ProjectEndpointPanelViewModel Source { get; } =
        new("Source", isTarget: false);

    public ProjectEndpointPanelViewModel Target { get; } =
        new("Target", isTarget: true);

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
    public static ProjectSetupViewModel FromProject(DbDeltaProject? p)
    {
        ProjectSetupViewModel vm = new();
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
            ProjectEndpointPanelViewModel.FromEndpoint(p.Source, "Source", isTarget: false);
        var tgt =
            ProjectEndpointPanelViewModel.FromEndpoint(p.Target, "Target", isTarget: true);

        // Copy fields into the pre-wired panels so PropertyChanged subscriptions
        // remain intact.
        CopyEndpoint(src, vm.Source);
        CopyEndpoint(tgt, vm.Target);

        return vm;
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
