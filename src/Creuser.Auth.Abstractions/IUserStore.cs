namespace Creuser.Auth.Abstractions;

public interface IUserStore
{
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Count users with a given role, scoped to active users by default.
    /// Drives the "last admin" guard on role-toggle and delete endpoints.
    /// </summary>
    Task<int> CountByRoleAsync(string role, bool activeOnly = true, CancellationToken ct = default);

    Task<IReadOnlyList<User>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task SaveAsync(User user, CancellationToken ct = default);
    Task UpdateLastLoginAsync(Guid id, DateTime when, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete a user. Cascades to <c>cr.workspace_members</c> via FK.
    /// Returns false if no row matched. Audit references (entity rows with
    /// <c>updated_by</c>) become orphaned UUIDs; that's deliberate — we keep
    /// the trail of <em>what happened</em> at the cost of <em>who did it</em>
    /// once they're gone.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
