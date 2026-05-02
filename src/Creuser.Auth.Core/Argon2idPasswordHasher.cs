using System.Security.Cryptography;
using System.Text;
using Creuser.Auth.Abstractions;
using Konscious.Security.Cryptography;

namespace Creuser.Auth.Core;

/// <summary>
/// Argon2id with parameters tuned to ~250ms hashing time on commodity hardware.
/// Hash format: argon2id:m={mem}:t={iter}:p={par}:{saltB64}:{hashB64}
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MemoryKb = 65536; // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 4;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism);
        return $"argon2id:m={MemoryKb}:t={Iterations}:p={Parallelism}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hashString)
    {
        var parts = hashString.Split(':');
        if (parts.Length != 6 || parts[0] != "argon2id")
            return false;
        if (
            !TryParseParam(parts[1], "m=", out var mem)
            || !TryParseParam(parts[2], "t=", out var iter)
            || !TryParseParam(parts[3], "p=", out var par)
        )
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            expected = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Compute(password, salt, mem, iter, par);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memKb, int iter, int par)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = par,
            Iterations = iter,
            MemorySize = memKb,
        };
        return argon2.GetBytes(HashBytes);
    }

    private static bool TryParseParam(string segment, string prefix, out int value)
    {
        value = 0;
        return segment.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(segment.AsSpan(prefix.Length), out value);
    }
}
