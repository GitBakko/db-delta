using Testcontainers.MsSql;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// Shared SQL Server container fixture used by all integration tests in this assembly.
/// </summary>
public sealed class LiveDbFixture : IAsyncLifetime, IAsyncDisposable
{
    public MsSqlContainer Container { get; } = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Y0urStrong!Pass")
        .Build();

    public string ConnectionString => Container.GetConnectionString() + ";TrustServerCertificate=True;";

    public ValueTask InitializeAsync() => new(Container.StartAsync());

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

[CollectionDefinition(nameof(LiveDbCollection))]
public sealed class LiveDbCollection : ICollectionFixture<LiveDbFixture> { }
