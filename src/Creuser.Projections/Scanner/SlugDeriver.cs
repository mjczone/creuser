using System.Globalization;
using System.Text.RegularExpressions;
using Creuser.Core.Projections;

namespace Creuser.Projections.Scanner;

/// <summary>
/// Derives a stable per-entity slug from a file path + frontmatter, per
/// the convention's <see cref="ConventionSlugSpec"/>. The slug is the
/// natural identifier in the workspace's <c>(kind, slug)</c> namespace.
/// </summary>
public static class SlugDeriver
{
    public static string Derive(
        ConventionSlugSpec spec,
        string relativePath,
        IReadOnlyDictionary<string, object?>? frontmatter
    )
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(relativePath).TrimStart('.');
        var fileDir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        // parent_dir is the immediate-parent folder name (e.g. for
        // `business-rules/auth/login.md`, parent_dir = `auth`). file_dir is
        // the full directory path; both are exposed as separate variables
        // so templates can pick the granularity they need.
        var parentDir = string.IsNullOrEmpty(fileDir) ? string.Empty : Path.GetFileName(fileDir);
        var pathSlug = relativePath.Replace('\\', '/');

        string raw;
        if (string.Equals(spec.From, "template", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(spec.Template))
                throw new InvalidOperationException(
                    "slug.from = 'template' but no `template:` provided."
                );
            raw = Interpolate(
                spec.Template,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["filename"] = fileName,
                    ["extension"] = ext,
                    ["parent_dir"] = parentDir,
                    ["file_dir"] = fileDir,
                    ["path"] = pathSlug,
                }
            );
        }
        else if (spec.From.StartsWith("frontmatter.", StringComparison.OrdinalIgnoreCase))
        {
            var key = spec.From[("frontmatter.".Length)..];
            if (frontmatter is null || !frontmatter.TryGetValue(key, out var v) || v is null)
                throw new InvalidOperationException(
                    $"slug.from = 'frontmatter.{key}' but the file has no such frontmatter key."
                );
            raw = v.ToString() ?? string.Empty;
        }
        else if (string.Equals(spec.From, "path", StringComparison.OrdinalIgnoreCase))
        {
            // Trim extension when "path" is used so renaming `.md` → `.mdx`
            // doesn't break the slug. The full extension is recoverable via
            // the entity's `path` field.
            var withoutExt = string.IsNullOrEmpty(ext) ? pathSlug : pathSlug[..^(ext.Length + 1)];
            raw = withoutExt.Replace('/', '-');
        }
        else
        {
            // default: filename
            raw = fileName;
        }

        return ApplyTransform(raw, spec.Transform);
    }

    private static string ApplyTransform(string raw, string transform) =>
        transform.ToLowerInvariant() switch
        {
            "kebab" => Kebab(raw),
            "snake" => Snake(raw),
            "lower" => raw.ToLowerInvariant(),
            "as-is" or "" => raw,
            _ => raw,
        };

    private static string Kebab(string s) =>
        Regex.Replace(s.Trim(), @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();

    private static string Snake(string s) =>
        Regex.Replace(s.Trim(), @"[^A-Za-z0-9]+", "_").Trim('_').ToLowerInvariant();

    private static string Interpolate(string template, IDictionary<string, string> vars)
    {
        return Regex.Replace(
            template,
            @"\{([a-zA-Z_][a-zA-Z0-9_]*)\}",
            m =>
                vars.TryGetValue(m.Groups[1].Value, out var v)
                    ? v
                    : throw new InvalidOperationException(
                        $"slug template references unknown variable '{m.Groups[1].Value}'."
                    )
        );
    }
}
