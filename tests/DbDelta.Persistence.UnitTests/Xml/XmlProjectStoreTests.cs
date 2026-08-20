using DbDelta.Core.Abstractions;
using DbDelta.Core.Options;
using DbDelta.Persistence.Xml;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Xml;

public class XmlProjectStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public XmlProjectStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dbdelta-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "demo.dbd");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Legacy (v1) tests — must remain unchanged ──────────────────────────

    [Fact]
    public async Task Save_then_Load_round_trips_all_fields()
    {
        XmlProjectStore store = new();
        DbDeltaProject project = new(
            Name: "Customer rollout",
            SourceConnectionId: Guid.Parse("9f2c1d76-1111-1111-1111-111111111111"),
            TargetConnectionId: Guid.Parse("3a55ee99-2222-2222-2222-222222222222"),
            Options: ComparisonOptions.Default,
            SelectedObjects: ["dbo.Customer", "dbo.vCustomer"]);

        await store.SaveAsync(_file, project, CancellationToken.None);
        DbDeltaProject back = await store.LoadAsync(_file, CancellationToken.None);

        back.Name.Should().Be(project.Name);
        back.SourceConnectionId.Should().Be(project.SourceConnectionId);
        back.TargetConnectionId.Should().Be(project.TargetConnectionId);
        back.Options.Should().Be(project.Options);
        back.SelectedObjects.Should().BeEquivalentTo(project.SelectedObjects);
    }

    [Fact]
    public async Task Save_writes_canonical_namespace()
    {
        XmlProjectStore store = new();
        DbDeltaProject project = new(
            Name: "x",
            SourceConnectionId: Guid.NewGuid(),
            TargetConnectionId: Guid.NewGuid(),
            Options: ComparisonOptions.Default,
            SelectedObjects: null);
        await store.SaveAsync(_file, project, CancellationToken.None);
        string text = await File.ReadAllTextAsync(_file, CancellationToken.None);
        text.Should().Contain("xmlns=\"https://schemas.dbdelta.org/project/v1\"");
    }

    // ── V2 tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task V2_round_trip_all_sections()
    {
        XmlProjectStore store = new();
        var connId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        DateTime created = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime modified = new(2026, 5, 21, 12, 34, 56, DateTimeKind.Utc);

        ProjectConnectionRef connRef = new(
            Id: connId,
            Name: "Dev-Source",
            ServerName: "sql-dev-01",
            DatabaseName: "AdventureWorks",
            EnvironmentTag: "DEV",
            EnvironmentColorHex: "#1E90FF");

        ProjectAuthentication auth = new(
            Mode: AuthenticationMode.SqlServer,
            UserName: "sa",
            RememberCredentials: true,
            Encrypt: false,
            TrustServerCertificate: true);

        ProjectEndpoint endpoint = new(connRef, auth);

        ProjectConnectionRef connRefTgt = new(
            Id: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Name: "Prod-Target",
            ServerName: "sql-prod-01",
            DatabaseName: "AdventureWorks",
            EnvironmentTag: "PROD",
            EnvironmentColorHex: "#FF4500");

        ProjectAuthentication authTgt = new(
            Mode: AuthenticationMode.WindowsIntegrated,
            UserName: null,
            RememberCredentials: false,
            Encrypt: true,
            TrustServerCertificate: false);

        ProjectEndpoint endpointTgt = new(connRefTgt, authTgt);

        Dictionary<ObjectSelectionKey, bool> selections = new()
        {
            [new ObjectSelectionKey("Table", "dbo", "Orders")] = true,
            [new ObjectSelectionKey("Table", "dbo", "Product")] = false,
            [new ObjectSelectionKey("View", "dbo", "vOrders")] = true,
        };

        DbDeltaProject project = new(
            Name: "Full v2 project",
            CreatedUtc: created,
            LastModifiedUtc: modified,
            Source: endpoint,
            Target: endpointTgt,
            Selections: selections);

        await store.SaveAsync(_file, project, CancellationToken.None);
        DbDeltaProject back = await store.LoadAsync(_file, CancellationToken.None);

        back.Name.Should().Be(project.Name);
        back.CreatedUtc.Should().BeCloseTo(project.CreatedUtc, TimeSpan.FromSeconds(1));
        back.LastModifiedUtc.Should().BeCloseTo(project.LastModifiedUtc, TimeSpan.FromSeconds(1));

        back.Source.Should().NotBeNull();
        back.Source!.Connection.Id.Should().Be(connRef.Id);
        back.Source.Connection.Name.Should().Be(connRef.Name);
        back.Source.Connection.ServerName.Should().Be(connRef.ServerName);
        back.Source.Connection.DatabaseName.Should().Be(connRef.DatabaseName);
        back.Source.Connection.EnvironmentTag.Should().Be(connRef.EnvironmentTag);
        back.Source.Connection.EnvironmentColorHex.Should().Be(connRef.EnvironmentColorHex);
        back.Source.Authentication.Mode.Should().Be(AuthenticationMode.SqlServer);
        back.Source.Authentication.UserName.Should().Be("sa");
        back.Source.Authentication.RememberCredentials.Should().BeTrue();
        back.Source.Authentication.Encrypt.Should().BeFalse();
        back.Source.Authentication.TrustServerCertificate.Should().BeTrue();

        back.Target.Should().NotBeNull();
        back.Target!.Authentication.Mode.Should().Be(AuthenticationMode.WindowsIntegrated);
        back.Target.Authentication.Encrypt.Should().BeTrue();

        back.Selections.Should().BeEquivalentTo(project.Selections);
    }

    [Fact]
    public async Task Read_legacy_v1_xml_produces_valid_project_with_defaults()
    {
        var srcId = Guid.Parse("9f2c1d76-1111-1111-1111-111111111111");
        var tgtId = Guid.Parse("3a55ee99-2222-2222-2222-222222222222");

        // Hand-crafted v1 XML — no schema attribute, uses XmlSerializer layout.
        // Options element must use the named enum member; "None" = 0 is the
        // simplest valid value for XmlSerializer with a [Flags] enum.
        string v1Xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <DbDeltaProject xmlns="https://schemas.dbdelta.org/project/v1" name="LegacyProject">
              <SourceConnectionId>{srcId:D}</SourceConnectionId>
              <TargetConnectionId>{tgtId:D}</TargetConnectionId>
              <Options>None</Options>
            </DbDeltaProject>
            """;

        await File.WriteAllTextAsync(_file, v1Xml, CancellationToken.None);

        XmlProjectStore store = new();
        DbDeltaProject project = await store.LoadAsync(_file, CancellationToken.None);

        project.Name.Should().Be("LegacyProject");
        project.SourceConnectionId.Should().Be(srcId);
        project.TargetConnectionId.Should().Be(tgtId);
        project.Source.Should().BeNull();
        project.Target.Should().BeNull();
        project.Selections.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_v2_emits_schema_2_attribute_and_Selections_section()
    {
        XmlProjectStore store = new();
        Dictionary<ObjectSelectionKey, bool> selections = new()
        {
            [new ObjectSelectionKey("Table", "dbo", "Orders")] = true,
        };

        DbDeltaProject project = new(
            Name: "AttrTest",
            CreatedUtc: DateTime.UtcNow,
            LastModifiedUtc: DateTime.UtcNow,
            Selections: selections);

        await store.SaveAsync(_file, project, CancellationToken.None);
        string text = await File.ReadAllTextAsync(_file, CancellationToken.None);

        text.Should().Contain("schema=\"2\"");
        text.Should().Contain("<Selections");
        text.Should().Contain("type=\"Table\"");
        text.Should().Contain("name=\"Orders\"");
    }

    /// <summary>
    /// A <c>.dbd</c> written before 2026-08-20 still loads. It carries
    /// <c>&lt;OwnerMappings&gt;</c>, <c>&lt;TableMappings&gt;</c> and
    /// <c>&lt;Options&gt;</c>, which nothing reads any more — they were saved
    /// and read back and consulted by no engine, and were deleted rather than
    /// implemented.
    /// </summary>
    /// <remarks>
    /// This is the whole compatibility contract of that deletion: an old file
    /// opens, and stops carrying those elements the next time it is saved. If
    /// the reader ever starts refusing what it does not recognise, this fails
    /// and it is right.
    /// </remarks>
    [Fact]
    public async Task A_project_saved_before_the_dead_options_were_removed_still_loads()
    {
        string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <DbDeltaProject xmlns="https://schemas.dbdelta.org/project/v1" schema="2">
              <Name>Vecchio progetto</Name>
              <CreatedUtc>2026-05-01T00:00:00Z</CreatedUtc>
              <LastModifiedUtc>2026-05-01T00:00:00Z</LastModifiedUtc>
              <OwnerMappings>
                <Map source="dbo" target="sales" />
              </OwnerMappings>
              <TableMappings>
                <Map sourceSchema="dbo" sourceName="Orders" targetSchema="sales" targetName="Ordini" />
              </TableMappings>
              <Options ignoreFillFactor="true" ignoreCollation="true" ignoreWhitespace="true"
                       ignoreCommentBlocks="false" treatExtendedPropertiesAsObjects="true" />
              <Selections>
                <Entry type="Table" schema="dbo" name="Orders" selected="true" />
              </Selections>
            </DbDeltaProject>
            """;
        await File.WriteAllTextAsync(_file, xml, CancellationToken.None);

        DbDeltaProject project = await new XmlProjectStore().LoadAsync(_file, CancellationToken.None);

        project.Name.Should().Be("Vecchio progetto");
        project.Selections.Should().ContainSingle()
               .Which.Key.Should().Be(new ObjectSelectionKey("Table", "dbo", "Orders"));
    }

    [Fact]
    public async Task Selections_key_uniqueness_single_entry_round_trips()
    {
        // IReadOnlyDictionary<ObjectSelectionKey, bool> guarantees key uniqueness
        // by construction.  This test verifies a single entry survives a write/read.
        XmlProjectStore store = new();
        ObjectSelectionKey key = new("StoredProcedure", "hr", "usp_GetEmployee");

        Dictionary<ObjectSelectionKey, bool> selections = new()
        {
            [key] = false,
        };

        DbDeltaProject project = new(
            Name: "UniqueKeyTest",
            CreatedUtc: DateTime.UtcNow,
            LastModifiedUtc: DateTime.UtcNow,
            Selections: selections);

        await store.SaveAsync(_file, project, CancellationToken.None);
        DbDeltaProject back = await store.LoadAsync(_file, CancellationToken.None);

        back.Selections.Should().ContainSingle();
        back.Selections[key].Should().BeFalse();
    }
}
