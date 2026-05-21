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
}
