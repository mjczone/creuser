using System.Security.Cryptography;

namespace Creuser.Web.Branding;

/// <summary>
/// Owns the on-disk location for branded assets (logo, favicon, login bg).
/// Files are content-addressed by SHA-256 so each upload has a stable URL
/// and old files can be GC'd later by listing the directory and dropping
/// any name not currently referenced by <c>BrandingConfig</c>.
/// </summary>
public sealed class BrandingAssetsService
{
    /// <summary>Public URL prefix served via <c>UseStaticFiles</c> in <c>Program.cs</c>.</summary>
    public const string UrlPrefix = "/api/branding/assets";

    private static readonly HashSet<string> AllowedExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".svg",
        ".ico",
    };

    private static readonly HashSet<string> AllowedContentTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/svg+xml",
        "image/x-icon",
        "image/vnd.microsoft.icon",
    };

    private const long MaxBytes = 2 * 1024 * 1024; // 2 MiB

    public string DirectoryPath { get; }

    public BrandingAssetsService(string dataDir)
    {
        DirectoryPath = Path.Combine(dataDir, "branding");
        Directory.CreateDirectory(DirectoryPath);
    }

    public bool IsAllowedExtension(string ext) => AllowedExtensions.Contains(ext);

    public bool IsAllowedContentType(string? ct) =>
        ct is not null && AllowedContentTypes.Contains(ct);

    public bool IsWithinSizeLimit(long size) => size > 0 && size <= MaxBytes;

    public long MaxBytesAllowed => MaxBytes;

    /// <summary>
    /// Hash + persist the supplied bytes. Returns the public URL the SPA can
    /// store in <c>BrandingConfig.LogoUrl</c>. The <paramref name="kind"/>
    /// becomes a filename prefix (<c>logo</c>, <c>favicon</c>, etc.) so it's
    /// easy to tell at a glance what each file is.
    /// </summary>
    public async Task<string> SaveAsync(
        string kind,
        byte[] bytes,
        string extension,
        CancellationToken ct = default
    )
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var filename = $"{kind}-{hash[..16]}{extension.ToLowerInvariant()}";
        var path = Path.Combine(DirectoryPath, filename);
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, bytes, ct);
        return $"{UrlPrefix}/{filename}";
    }
}
