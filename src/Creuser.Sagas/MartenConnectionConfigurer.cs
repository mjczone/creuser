using Marten;
using Microsoft.Extensions.Configuration;

namespace Creuser.Sagas;

/// <summary>
/// Late-binds the Postgres connection string into Marten's <see cref="StoreOptions"/>
/// at service-provider build time. Required because reading
/// <c>builder.Configuration.GetConnectionString("Postgres")</c> at the top
/// of <c>Program.cs</c> happens BEFORE <see cref="WebApplicationFactory"/>'s
/// <c>ConfigureAppConfiguration</c> overrides apply, so test fixtures
/// otherwise see the production connection string instead of the
/// Testcontainers-spun-up one.
///
/// <para>
/// Marten's <c>IConfigureMarten</c> contract resolves at host build time
/// — by then all configuration sources (including the WAF's in-memory
/// override) are merged. The connection string set here is the one Marten
/// actually uses.
/// </para>
/// </summary>
public sealed class MartenConnectionConfigurer : IConfigureMarten
{
    private readonly IConfiguration _configuration;

    public MartenConnectionConfigurer(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        var conn = _configuration.GetConnectionString("Postgres") ?? string.Empty;
        options.Connection(conn);
    }
}
