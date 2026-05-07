using System.Security.Cryptography;
using System.Text;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Creuser.Projections.Conventions;

/// <summary>
/// Default <see cref="IConventionLoader"/>: walks
/// <c>.creuser/conventions/*.{yaml,yml}</c> in the working tree, resolves
/// <c>extends:</c> against <see cref="StandardConventions"/>, and returns
/// the merged conventions ready for the scanner.
/// </summary>
public sealed class ConventionLoader : IConventionLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public Task<ConventionLoadResult> LoadAsync(
        Workspace workspace,
        string workingTreePath,
        CancellationToken ct = default
    )
    {
        var errors = new List<ConventionLoadError>();
        var conventions = new List<Convention>();

        if (string.IsNullOrEmpty(workingTreePath) || !Directory.Exists(workingTreePath))
        {
            errors.Add(
                new ConventionLoadError(
                    null,
                    "Working tree does not exist; conventions cannot be loaded."
                )
            );
            return Task.FromResult(new ConventionLoadResult(conventions, errors));
        }

        var conventionsDir = Path.Combine(workingTreePath, ".creuser", "conventions");
        if (!Directory.Exists(conventionsDir))
        {
            // No convention dir is a valid state — workspace just hasn't
            // declared any. Empty result, no error.
            return Task.FromResult(new ConventionLoadResult(conventions, errors));
        }

        foreach (
            var path in Directory
                .EnumerateFiles(conventionsDir)
                .Where(p =>
                {
                    var ext = Path.GetExtension(p);
                    var name = Path.GetFileName(p);
                    return (
                            ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                        ) && !name.StartsWith('_');
                })
                .OrderBy(p => p, StringComparer.Ordinal)
        )
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(workingTreePath, path).Replace('\\', '/');
            try
            {
                var raw = File.ReadAllText(path);
                var (conv, err) = Parse(raw, rel);
                if (err is not null)
                    errors.Add(err);
                if (conv is not null)
                    conventions.Add(conv);
            }
            catch (Exception ex)
            {
                errors.Add(new ConventionLoadError(rel, ex.Message));
            }
        }

        return Task.FromResult(new ConventionLoadResult(conventions, errors));
    }

    /// <summary>
    /// Parse a single convention YAML, including <c>extends:</c> resolution.
    /// Public so the validate-only endpoint can call it without a workspace.
    /// </summary>
    public static (Convention? Convention, ConventionLoadError? Error) Parse(
        string yaml,
        string? sourcePath
    )
    {
        ConventionDoc? doc;
        try
        {
            doc = Deserializer.Deserialize<ConventionDoc>(yaml);
        }
        catch (Exception ex)
        {
            return (null, new ConventionLoadError(sourcePath, $"YAML parse error: {ex.Message}"));
        }
        if (doc is null)
            return (null, new ConventionLoadError(sourcePath, "Empty convention document."));

        if (!string.IsNullOrWhiteSpace(doc.Extends))
        {
            if (!StandardConventions.TryGet(doc.Extends, out var baseYaml))
                return (
                    null,
                    new ConventionLoadError(sourcePath, $"Unknown extends target: {doc.Extends}.")
                );
            ConventionDoc? baseDoc;
            try
            {
                baseDoc = Deserializer.Deserialize<ConventionDoc>(baseYaml);
            }
            catch (Exception ex)
            {
                return (
                    null,
                    new ConventionLoadError(
                        sourcePath,
                        $"Failed to parse base convention '{doc.Extends}': {ex.Message}"
                    )
                );
            }
            if (baseDoc is not null)
                doc = MergeOnto(doc, baseDoc);
        }

        if (string.IsNullOrWhiteSpace(doc.Id))
            return (null, new ConventionLoadError(sourcePath, "Convention is missing `id`."));
        if (doc.Match is null || string.IsNullOrWhiteSpace(doc.Match.Glob))
            return (
                null,
                new ConventionLoadError(sourcePath, "Convention is missing `match.glob`.")
            );

        var convention = new Convention(
            Id: doc.Id,
            Description: doc.Description,
            Extends: doc.Extends,
            Priority: doc.Priority ?? 0,
            Match: new ConventionMatch(
                Glob: doc.Match.Glob,
                Exclude: doc.Match.Exclude ?? new List<string>(),
                FrontmatterMustHave: doc.Match.FrontmatterMustHave ?? new List<string>()
            ),
            Slug: BuildSlugSpec(doc.Slug),
            Metadata: BuildMetadataSpec(doc.Metadata),
            Relationships: (doc.Relationships ?? new List<RelationshipDoc>())
                .Select(r => BuildRelationship(r))
                .ToList(),
            Validation: (doc.Validation ?? new List<ValidationDoc>())
                .Select(v => new ConventionValidationRule(
                    Rule: v.Rule ?? string.Empty,
                    Expr: v.Expr ?? string.Empty
                ))
                .ToList(),
            Actions: (doc.Actions ?? new List<ActionDoc>())
                .Where(a => !string.IsNullOrWhiteSpace(a.Id) && a.Runs is not null)
                .Select(a => new ConventionAction(
                    Id: a.Id!,
                    Label: a.Label ?? a.Id!,
                    Icon: a.Icon,
                    When: a.When,
                    Confirm: a.Confirm,
                    Runs: new ConventionActionRuns(
                        Kind: a.Runs!.Kind ?? string.Empty,
                        Script: a.Runs.Script,
                        Prompt: a.Runs.Prompt,
                        Tool: a.Runs.Tool,
                        Args: a.Runs.Args,
                        JobId: a.Runs.JobId,
                        Output: a.Runs.Output is null
                            ? null
                            : new ConventionActionOutput(a.Runs.Output.Target ?? string.Empty)
                    )
                ))
                .ToList(),
            ContentHash: Sha256(yaml),
            SourcePath: sourcePath
        );
        return (convention, null);
    }

    private static ConventionRelationship BuildRelationship(RelationshipDoc r)
    {
        var kind = r.Kind ?? string.Empty;
        var name = !string.IsNullOrWhiteSpace(r.Name) ? r.Name! : Humanize(kind);
        var inverse = r.Inverse;
        string? inverseName;
        if (!string.IsNullOrWhiteSpace(r.InverseName))
            inverseName = r.InverseName;
        else if (string.IsNullOrWhiteSpace(inverse))
            inverseName = null;
        else if (string.Equals(inverse, kind, StringComparison.Ordinal))
            inverseName = name; // symmetric edge: reverse folder mirrors the forward folder
        else
            inverseName = Humanize(inverse!);

        var (source, defaultInterpret) = BuildSource(r);
        var interpret = ParseInterpret(r.Interpret) ?? defaultInterpret;
        var filter = BuildFilter(r.Filter);
        var targetKind = BuildTargetKind(r.TargetKind);

        return new ConventionRelationship(
            Kind: kind,
            Name: name,
            Icon: r.Icon,
            Description: r.Description,
            Order: r.Order ?? 100,
            Source: source,
            Filter: filter,
            Interpret: interpret,
            TargetKind: targetKind,
            Metadata: r.Metadata,
            Inverse: inverse,
            InverseName: inverseName,
            InverseIcon: r.InverseIcon
        );
    }

    /// <summary>
    /// Build the <see cref="ConventionRefSource"/> from YAML, accepting both
    /// shorthand forms (<c>source: frontmatter.related</c>) and the legacy
    /// <c>select_path</c> / <c>select_frontmatter</c> keys. Returns the
    /// default <see cref="ConventionRefInterpret"/> implied by the source kind
    /// so legacy rules keep their original semantics.
    /// </summary>
    private static (
        ConventionRefSource Source,
        ConventionRefInterpret DefaultInterpret
    ) BuildSource(RelationshipDoc r)
    {
        // Legacy: select_path → path-template source, Path interpretation.
        if (!string.IsNullOrWhiteSpace(r.SelectPath))
        {
            return (
                new ConventionRefSource("path-template", r.SelectPath, null),
                ConventionRefInterpret.Path
            );
        }
        // Legacy: select_frontmatter → frontmatter source, Slug interpretation.
        if (!string.IsNullOrWhiteSpace(r.SelectFrontmatter))
        {
            return (
                new ConventionRefSource("frontmatter", r.SelectFrontmatter, null),
                ConventionRefInterpret.Slug
            );
        }
        // New shape: source as string shorthand (e.g. `frontmatter.related`, `glob:packages/db/**`).
        if (r.Source is string s && !string.IsNullOrWhiteSpace(s))
        {
            var (kind, key) = ParseSourceShorthand(s);
            return (new ConventionRefSource(kind, key, null), ConventionRefInterpret.Auto);
        }
        // New shape: source as object {kind, key, literals}.
        if (r.Source is IDictionary<object, object?> map)
        {
            var kind = map.TryGetValue("kind", out var k) ? k?.ToString() : null;
            var key = map.TryGetValue("key", out var v) ? v?.ToString() : null;
            IReadOnlyList<string>? literals = null;
            if (
                map.TryGetValue("literals", out var litRaw)
                && litRaw is IEnumerable<object?> litList
            )
            {
                literals = litList.Where(x => x is not null).Select(x => x!.ToString()!).ToList();
            }
            return (
                new ConventionRefSource(kind ?? string.Empty, key, literals),
                ConventionRefInterpret.Auto
            );
        }
        // Empty / no source declared. Yield an inert frontmatter source so the
        // resolver simply produces no edges for this rule.
        return (new ConventionRefSource("frontmatter", null, null), ConventionRefInterpret.Auto);
    }

    /// <summary>
    /// <c>frontmatter.related</c> → (frontmatter, related); <c>glob:packages/db/**</c>
    /// → (glob, packages/db/**); <c>body.links</c> → (body-links, null);
    /// <c>literal</c> → (literal, null).
    /// </summary>
    private static (string Kind, string? Key) ParseSourceShorthand(string s)
    {
        s = s.Trim();
        // Colon-prefix forms: glob:..., path-template:..., literal:...
        var colon = s.IndexOf(':');
        if (colon > 0 && colon < s.Length - 1)
        {
            var prefix = s[..colon];
            if (
                prefix.Equals("glob", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("path-template", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("literal", StringComparison.OrdinalIgnoreCase)
            )
            {
                return (prefix.ToLowerInvariant(), s[(colon + 1)..].Trim());
            }
        }
        // Dotted forms: frontmatter.<key>, body.<sub>, etc.
        var dot = s.IndexOf('.');
        if (dot > 0)
        {
            var prefix = s[..dot];
            var rest = s[(dot + 1)..].Trim();
            if (prefix.Equals("frontmatter", StringComparison.OrdinalIgnoreCase))
                return ("frontmatter", rest);
            if (prefix.Equals("body", StringComparison.OrdinalIgnoreCase))
                return ($"body-{rest.Replace('_', '-')}", null);
        }
        // Bare token: treat as a frontmatter key.
        return ("frontmatter", s);
    }

    private static ConventionRefInterpret? ParseInterpret(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => ConventionRefInterpret.Auto,
            "path" => ConventionRefInterpret.Path,
            "slug" => ConventionRefInterpret.Slug,
            "glob" => ConventionRefInterpret.Glob,
            "url" => ConventionRefInterpret.Url,
            "ref-object" or "ref_object" or "refobject" => ConventionRefInterpret.RefObject,
            _ => null,
        };
    }

    private static ConventionRefFilter? BuildFilter(object? raw)
    {
        if (raw is null)
            return null;
        if (raw is IDictionary<object, object?> map)
        {
            var kind = map.TryGetValue("kind", out var k) ? k?.ToString() : null;
            var pattern = map.TryGetValue("pattern", out var p) ? p?.ToString() : null;
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(pattern))
                return null;
            return new ConventionRefFilter(kind!.ToLowerInvariant(), pattern!);
        }
        return null;
    }

    /// <summary>
    /// Accepts: <c>any</c>, a single kind id (<c>adr</c>), or a list
    /// (<c>[adr, plan]</c>). Legacy YAML wrote a single nullable string;
    /// null/empty translates to a typed-allowed list with one empty entry —
    /// preserved so legacy slug lookups against <c>(target_kind="", slug)</c>
    /// remain unchanged for now.
    /// </summary>
    private static ConventionRefTargetKind BuildTargetKind(object? raw)
    {
        if (raw is null)
            return ConventionRefTargetKind.AnyKind;
        if (raw is string s)
        {
            s = s.Trim();
            if (string.IsNullOrEmpty(s) || s.Equals("any", StringComparison.OrdinalIgnoreCase))
                return ConventionRefTargetKind.AnyKind;
            return new ConventionRefTargetKind(false, new[] { s });
        }
        if (raw is IEnumerable<object?> list)
        {
            var allowed = list.Where(x => x is not null)
                .Select(x => x!.ToString()!.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
            if (allowed.Count == 0)
                return ConventionRefTargetKind.AnyKind;
            return new ConventionRefTargetKind(false, allowed);
        }
        return ConventionRefTargetKind.AnyKind;
    }

    /// <summary>
    /// Snake_case kind → Title Case display name. <c>related_adrs</c> → <c>Related Adrs</c>.
    /// Authors who want a different casing override <c>name:</c> in YAML; this only fires
    /// when the field is omitted.
    /// </summary>
    private static string Humanize(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return string.Empty;
        var parts = kind.Split(
            new[] { '_', '-' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return string.Join(
            ' ',
            parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])
        );
    }

    private static ConventionSlugSpec BuildSlugSpec(SlugDoc? d)
    {
        if (d is null)
            return new ConventionSlugSpec("filename", "as-is", null);
        return new ConventionSlugSpec(
            From: d.From ?? "filename",
            Transform: d.Transform ?? "as-is",
            Template: d.Template
        );
    }

    private static ConventionMetadataSpec BuildMetadataSpec(MetadataDoc? d)
    {
        if (d is null)
            return new ConventionMetadataSpec(
                "frontmatter",
                new Dictionary<string, string>(),
                new List<string>()
            );
        return new ConventionMetadataSpec(
            Source: d.Source ?? "frontmatter",
            Computed: d.Computed ?? new Dictionary<string, string>(),
            Required: d.Required ?? new List<string>()
        );
    }

    /// <summary>
    /// Shallow merge: base values fill in only when the override didn't
    /// provide them. Lists from the override fully replace base lists; this
    /// is intentional so a workspace can narrow a glob without inheriting
    /// the base's wide one.
    /// </summary>
    private static ConventionDoc MergeOnto(ConventionDoc over, ConventionDoc baseDoc)
    {
        return new ConventionDoc
        {
            Id = !string.IsNullOrWhiteSpace(over.Id) ? over.Id : baseDoc.Id,
            Description = over.Description ?? baseDoc.Description,
            Extends = over.Extends,
            Priority = over.Priority ?? baseDoc.Priority,
            Match = over.Match ?? baseDoc.Match,
            Slug = over.Slug ?? baseDoc.Slug,
            Metadata = over.Metadata ?? baseDoc.Metadata,
            Relationships = over.Relationships ?? baseDoc.Relationships,
            Validation = over.Validation ?? baseDoc.Validation,
            Actions = over.Actions ?? baseDoc.Actions,
        };
    }

    private static string Sha256(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// YAML-mapped DTO. Lower-case property names match the snake-case
    /// naming convention; YamlDotNet's UnderscoredNamingConvention adapts.
    /// </summary>
    private sealed class ConventionDoc
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? Extends { get; set; }
        public int? Priority { get; set; }
        public MatchDoc? Match { get; set; }
        public SlugDoc? Slug { get; set; }
        public MetadataDoc? Metadata { get; set; }
        public List<RelationshipDoc>? Relationships { get; set; }
        public List<ValidationDoc>? Validation { get; set; }
        public List<ActionDoc>? Actions { get; set; }
    }

    private sealed class MatchDoc
    {
        public string? Glob { get; set; }
        public List<string>? Exclude { get; set; }
        public List<string>? FrontmatterMustHave { get; set; }
    }

    private sealed class SlugDoc
    {
        public string? From { get; set; }
        public string? Transform { get; set; }
        public string? Template { get; set; }
    }

    private sealed class MetadataDoc
    {
        public string? Source { get; set; }
        public Dictionary<string, string>? Computed { get; set; }
        public List<string>? Required { get; set; }
    }

    private sealed class RelationshipDoc
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public string? Description { get; set; }
        public int? Order { get; set; }

        // New shape — accept string shorthand or object form. YamlDotNet
        // deserializes both into `object?` (string vs Dictionary<object,object?>).
        public object? Source { get; set; }
        public object? Filter { get; set; }
        public string? Interpret { get; set; }
        public object? TargetKind { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }

        // Legacy shape — translated to Source/Interpret in BuildRelationship.
        public string? SelectPath { get; set; }
        public string? SelectFrontmatter { get; set; }

        public string? Inverse { get; set; }
        public string? InverseName { get; set; }
        public string? InverseIcon { get; set; }
    }

    private sealed class ValidationDoc
    {
        public string? Rule { get; set; }
        public string? Expr { get; set; }
    }

    private sealed class ActionDoc
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public string? Icon { get; set; }
        public string? When { get; set; }
        public string? Confirm { get; set; }
        public ActionRunsDoc? Runs { get; set; }
    }

    private sealed class ActionRunsDoc
    {
        public string? Kind { get; set; }
        public string? Script { get; set; }
        public string? Prompt { get; set; }
        public string? Tool { get; set; }
        public Dictionary<string, string>? Args { get; set; }
        public string? JobId { get; set; }
        public ActionOutputDoc? Output { get; set; }
    }

    private sealed class ActionOutputDoc
    {
        public string? Target { get; set; }
    }
}
