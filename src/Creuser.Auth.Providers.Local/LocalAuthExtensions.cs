using Creuser.Auth.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Creuser.Auth.Providers.Local;

public static class LocalAuthExtensions
{
    /// <summary>
    /// Registers the local username+password provider as the default
    /// <see cref="IAuthProvider"/>. Call after <c>AddDatabase()</c> so the
    /// IUserStore is in place.
    /// </summary>
    public static IServiceCollection AddLocalAuth(this IServiceCollection services)
    {
        services.AddSingleton<IAuthProvider, LocalAuthProvider>();
        services.AddSingleton<LocalAuthProvider>();
        return services;
    }
}
