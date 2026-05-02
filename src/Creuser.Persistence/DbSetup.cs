using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Creuser.Persistence;

public static class DbSetup
{
    /// <summary>
    /// Registers the Postgres data source and persistence repositories.
    /// Reads <c>ConnectionStrings:Postgres</c>; throws on missing config so
    /// misconfiguration fails fast at startup.
    /// </summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services)
    {
        // Lazy registration: don't parse the connection string until the data
        // source is first requested. This lets build-time OpenAPI emission
        // (which constructs the host but never resolves the data source)
        // succeed without a configured database.
        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var conn = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(conn))
                throw new InvalidOperationException(
                    "Missing ConnectionStrings:Postgres. In dev, run `npm run services:up` to provision Postgres and write the connection string into appsettings.Development.local.json. In production, set the ConnectionStrings__Postgres environment variable."
                );
            return new NpgsqlDataSourceBuilder(conn).Build();
        });

        services.AddSingleton<IUserStore, usersRepository>();
        services.AddSingleton<BootstrapAdminService>();
        services.AddHostedService<DbInitializer>();

        return services;
    }
}
