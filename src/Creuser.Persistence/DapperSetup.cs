using Dapper;
using MJCZone.DapperMatic.TypeMapping;

namespace Creuser.Persistence;

/// <summary>
/// One-time Dapper / DapperMatic configuration. Call <see cref="Initialize"/>
/// before constructing any database connections.
/// </summary>
public static class DapperSetup
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        // Our entity classes use lowercase property names that match column
        // names exactly (see Tables/users.cs), so we don't need Dapper's
        // snake_case auto-mapping. Leaving MatchNamesWithUnderscores at its
        // default (false) avoids surprising mappings if any legacy entity
        // ever sneaks in with PascalCase properties.
        DefaultTypeMap.MatchNamesWithUnderscores = false;

        DapperMaticTypeMapping.Initialize();

        _initialized = true;
    }
}
