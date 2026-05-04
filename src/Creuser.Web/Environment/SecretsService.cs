using System.Runtime.InteropServices;
using Creuser.Core.Secrets;

namespace Creuser.Web.Environment;

/// <summary>
/// Owns the on-disk store for runtime secrets — API keys, SMTP passwords,
/// OAuth client secrets. Architecture-mandated layout:
///
///   <c>&lt;dataDir&gt;/secrets/&lt;name&gt;</c> — chmod 600, ASCII text.
///
/// The on-disk filename is the lookup key (e.g. <c>anthropic.key</c>,
/// <c>openai.key</c>, <c>smtp.password</c>). The DB stores only references
/// to these filenames in <c>cr.app_settings</c>; actual values are never
/// stored in Postgres or returned over the wire.
///
/// External callers (the Environment page) can <c>Set</c> a value or
/// <c>Delete</c> it, and check <c>Exists</c>. There's no public read —
/// only domain code that needs the secret (Anthropic provider, SMTP client,
/// etc.) calls <see cref="ReadInternal"/>.
/// </summary>
public sealed class SecretsService : ISecretsReader
{
    public string DirectoryPath { get; }

    public SecretsService(string dataDir)
    {
        DirectoryPath = Path.Combine(dataDir, "secrets");
        Directory.CreateDirectory(DirectoryPath);

        // Best-effort tighten the directory permissions on POSIX. On Windows
        // the file system inherits ACLs from the parent — admins running on
        // Windows are expected to lock down the data volume themselves.
        if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        )
        {
            try
            {
                File.SetUnixFileMode(
                    DirectoryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
            catch
            {
                // Non-fatal — the OS may not allow the chmod (e.g. mounted volume).
                // Operators are responsible for the underlying disk's permissions.
            }
        }
    }

    /// <summary>
    /// Persist a secret value. Names must be safe filenames — letters,
    /// digits, dot, hyphen, underscore. Empty/whitespace values clear the
    /// secret instead of writing.
    /// </summary>
    public async Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        EnsureSafeName(name);
        var path = Path.Combine(DirectoryPath, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        await File.WriteAllTextAsync(path, value.Trim(), ct);
        TryChmod600(path);
    }

    /// <summary>Returns true if the secret file exists (non-empty).</summary>
    public bool Exists(string name)
    {
        EnsureSafeName(name);
        var path = Path.Combine(DirectoryPath, name);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public bool Delete(string name)
    {
        EnsureSafeName(name);
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>List the secret filenames currently present.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(DirectoryPath))
            return Array.Empty<string>();
        return Directory
            .EnumerateFiles(DirectoryPath)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Internal-only read. Called by domain code that needs the key to make
    /// an outbound API call (Anthropic provider, SMTP client, etc.). Never
    /// expose this through an HTTP endpoint.
    /// </summary>
    public async Task<string?> ReadInternalAsync(string name, CancellationToken ct = default)
    {
        EnsureSafeName(name);
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path))
            return null;
        return (await File.ReadAllTextAsync(path, ct)).Trim();
    }

    /// <summary>
    /// <see cref="ISecretsReader"/> contract — same as
    /// <see cref="ReadInternalAsync"/> with a stable name plugins
    /// can target.
    /// </summary>
    Task<string?> ISecretsReader.ReadAsync(string name, CancellationToken ct) =>
        ReadInternalAsync(name, ct);

    private static void EnsureSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Secret name cannot be empty.", nameof(name));
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
                throw new ArgumentException(
                    $"Secret name '{name}' contains an unsafe character.",
                    nameof(name)
                );
        }
        if (name.StartsWith('.') || name.Contains(".."))
            throw new ArgumentException(
                $"Secret name '{name}' is not a safe filename.",
                nameof(name)
            );
    }

    private static void TryChmod600(string path)
    {
        if (
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        )
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Non-fatal — operator is responsible for volume-level perms.
        }
    }
}
