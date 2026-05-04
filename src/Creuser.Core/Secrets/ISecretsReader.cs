namespace Creuser.Core.Secrets;

/// <summary>
/// Read-only seam over the host's secret store
/// (<c>&lt;dataDir&gt;/secrets/</c>). Plugin runners and tools resolve
/// credentials through this interface — they declare the secret's
/// filename in their workspace settings or step inputs and read the
/// value at execution time. Secret VALUES never leave the server
/// process; the SPA stores filenames only.
///
/// <para>
/// The host's <c>SecretsService</c> implements this. Plugins inject
/// <c>ISecretsReader</c> via DI; they don't take a dependency on the
/// host's environment module.
/// </para>
/// </summary>
public interface ISecretsReader
{
    /// <summary>Read the secret's value. Returns null when the file is missing or empty.</summary>
    Task<string?> ReadAsync(string name, CancellationToken ct = default);

    /// <summary>True when the secret file exists and is non-empty.</summary>
    bool Exists(string name);
}
