using Creuser.Core.Repositories;

namespace Creuser.Web.Workspaces;

/// <summary>
/// Default <see cref="IWorkspaceProviderRegistry"/> — wraps the keyed-DI
/// lookup so endpoints don't take a direct <see cref="IServiceProvider"/>
/// dependency. Providers register themselves keyed by
/// <see cref="WorkspaceType"/>; this class is the single seam every
/// caller goes through.
/// </summary>
public sealed class WorkspaceProviderRegistry : IWorkspaceProviderRegistry
{
    private readonly IServiceProvider _sp;

    public WorkspaceProviderRegistry(IServiceProvider sp)
    {
        _sp = sp;
    }

    public IWorkspaceProvider Resolve(Workspace workspace) => Resolve(workspace.Type);

    public IWorkspaceProvider Resolve(string workspaceType)
    {
        var provider = _sp.GetKeyedService<IWorkspaceProvider>(workspaceType);
        if (provider is not null)
            return provider;
        throw new NotSupportedException(
            $"No IWorkspaceProvider registered for workspace type '{workspaceType}'."
        );
    }
}
