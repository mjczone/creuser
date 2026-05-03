using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production"); // skip Scalar, dev-only branches
        builder.ConfigureAppConfiguration(
            (_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = ConnectionString,
                    ["ConnectionStrings:Redis"] = "", // fall back to in-memory IDistributedCache
                    // Predictable bootstrap creds for tests.
                    ["CREUSER_BOOTSTRAP_EMAIL"] = "admin@creuser.test",
                    ["CREUSER_BOOTSTRAP_PASSWORD"] = "ChangeMe!",
                };
                if (SchedulerIntervalMs is int ms)
                {
                    settings["CREUSER_SCHEDULER_INTERVAL_MS"] = ms.ToString();
                }
                cfg.AddInMemoryCollection(settings);
            }
        );
    }
}
