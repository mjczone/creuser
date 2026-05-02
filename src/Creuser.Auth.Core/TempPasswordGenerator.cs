using System.Security.Cryptography;

namespace Creuser.Auth.Core;

/// <summary>
/// Generates strong random temporary passwords for the admin invite flow.
/// Used when an administrator creates a user without supplying their own
/// memorable temp password. The generated value is returned to the admin
/// once (in the create-user response) so they can pass it on out-of-band.
/// </summary>
public static class TempPasswordGenerator
{
    // Avoids visually ambiguous characters (0/O, 1/I/l) so the admin can
    // dictate the password over voice or transcribe it from a screenshot.
    private const string Alphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_+!?";

    public static string Generate(int length = 12)
    {
        if (length < 8)
            throw new ArgumentOutOfRangeException(nameof(length), "Minimum length is 8.");
        Span<char> buf = stackalloc char[length];
        Span<byte> rnd = stackalloc byte[length];
        RandomNumberGenerator.Fill(rnd);
        for (var i = 0; i < length; i++)
        {
            buf[i] = Alphabet[rnd[i] % Alphabet.Length];
        }
        return new string(buf);
    }
}
