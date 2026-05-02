using Testcontainers.PostgreSql;

namespace Creuser.Integration.Tests;

/// <summary>
/// Class fixture: spins up a real Postgres 17 + pgvector container once per
/// test class via Testcontainers, exposes the connection string, tears it
/// down at the end. Pulled image is reused across runs.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("creuser")
            .WithUsername("creuser")
            .WithPassword("creuser_test")
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
