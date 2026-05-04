using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Creuser.Integration.Tests;

/// <summary>
/// Boots the real Creuser.Web app in-memory against the Postgres provided
/// by <see cref="PostgresFixture"/>. Overrides ConnectionStrings:Postgres
/// via in-memory configuration so the production wiring (DapperMatic schema
/// init, bootstrap admin seed) runs against the test container.
/// </summary>
public sealed class CreuserApiFactory : WebApplicationFactory<Program>
{
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Override <c>CREUSER_DATA_DIR</c> for tests that need to stage
    /// data-directory contents (most notably plugins under
    /// <c>&lt;dataDir&gt;/plugins/</c>) before the host boots. Null
    /// leaves the default repo-relative <c>.data</c> directory in place.
    /// </summary>
    public string? DataDir { get; init; }

    /// <summary>
    /// Override <c>CREUSER_SCHEDULER_INTERVAL_MS</c> for tests that need
    /// the scheduler tick to fire inside test wall-time. Null leaves the
    /// 30s production default in place (no tick during a typical test).
    /// </summary>
    public int? SchedulerIntervalMs { get; init; }

    /// <summary>
    /// Optional hook for replacing services in the WAF's container — used
    /// by tests that need to swap in a stub <c>IChatClientResolver</c>
    /// (the llm-tool-loop suite) or other infra fakes. Mutates the same
    /// <see cref="IServiceCollection"/> the production wiring populated.
    /// </summary>
    public Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? ConfigureTestServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Test" environment opts out of durable local queues — Wolverine's
        // outbox + Testcontainers teardown race causes flake on Postgres
        // shutdown. Tests don't need durability; in-memory queueing
        // (Wolverine's default when durable queues are disabled) is fine.
        builder.UseEnvironment("Test");
        // CREUSER_DATA_DIR is read at the very top of Program.cs (before
        // builder.Build), so an in-memory config provider added via
        // ConfigureAppConfiguration runs too late. Setting the env var
        // before the host builds is the only way to override; safe here
        // because tests run serial (DisableTestParallelization).
        if (!string.IsNullOrEmpty(DataDir))
            Environment.SetEnvironmentVariable("CREUSER_DATA_DIR", DataDir);
        builder.ConfigureAppConfiguration(
            (_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = ConnectionString,
                    ["ConnectionStrings:Redis"] = "",
                    ["CREUSER_BOOTSTRAP_EMAIL"] = "admin@creuser.test",
                    ["CREUSER_BOOTSTRAP_PASSWORD"] = "ChangeMe!",
                };
                if (SchedulerIntervalMs is int ms)
                    settings["CREUSER_SCHEDULER_INTERVAL_MS"] = ms.ToString();
                if (!string.IsNullOrEmpty(DataDir))
                    settings["CREUSER_DATA_DIR"] = DataDir;
                cfg.AddInMemoryCollection(settings);
            }
        );
        if (ConfigureTestServices is { } configure)
        {
            builder.ConfigureTestServices(configure);
        }
    }

    /// <summary>
    /// Swallow <see cref="OperationCanceledException"/> raised by the
    /// Wolverine + Marten shutdown path. The race: when Testcontainers'
    /// Postgres exits while Wolverine's
    /// <c>MessageStoreCollection.ReleaseAllOwnershipAsync</c> is still
    /// running, Npgsql cancels the in-flight query and the cancellation
    /// bubbles up through <c>WebApplicationFactory.DisposeAsync</c>,
    /// failing the host test. <c>DurabilityMode.Solo</c> + the
    /// "Test" environment opt-out reduce the surface but don't eliminate
    /// it (Marten still registers a message store via
    /// <c>IntegrateWithWolverine()</c>). Since this only happens at host
    /// shutdown — long after test assertions have run — swallowing the
    /// cancellation is safe and matches what the test author would do
    /// manually.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on Wolverine shutdown vs Testcontainers teardown.
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Same race surfacing wrapped.
        }
    }
}
