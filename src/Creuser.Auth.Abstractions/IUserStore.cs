namespace Creuser.Auth.Abstractions;

public interface IUserStore
{
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task SaveAsync(User user, CancellationToken ct = default);
    Task UpdateLastLoginAsync(Guid id, DateTime when, CancellationToken ct = default);
}
