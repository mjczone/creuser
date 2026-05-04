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
                cfg.AddInMemoryCollection(settings);
            }
        );
        if (ConfigureTestServices is { } configure)
        {
            builder.ConfigureTestServices(configure);
        }
    }
}
