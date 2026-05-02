namespace Creuser.Auth.Abstractions;

public interface IPasswordHasher
{
    /// <summary>Returns a self-describing hash string (algorithm, params, salt, hash).</summary>
    string Hash(string password);

    /// <summary>Constant-time verification.</summary>
    bool Verify(string password, string hashString);
}
