using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Scripting;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Creuser.Projections.Scanner;

/// <summary>
/// Walks a workspace's working tree, applies the loaded conventions, and
/// emits the entity + ref projections ready for storage. No I/O against
/// the database — the sync service composes this with the entity store.
///
/// <para>
/// Two-pass design: the first pass produces entities (entity ids assigned
/// at this point), the second pass resolves relationships against the
/// already-built entity index. Refs that don't resolve persist with
/// <c>to_entity_id = null</c>.
/// </para>
/// </summary>
public sealed class ProjectionScanner
{
    private readonly TimeProvider _time;

    public ProjectionScanner(TimeProvider time)
    {
        _time = time;
    }

    public ScanResult Scan(
        Workspace workspace,
        string workingTreePath,
        IReadOnlyList<Convention> conventions
    )
    {
        var sw = Stopwatch.StartNew();
        var now = _time.GetUtcNow().UtcDateTime;
        var entities = new List<EntityProjection>();
        var entityById = new Dictionary<Guid, EntityProjection>();
        var entityByKindSlug = new Dictionary<(string kind, string slug), Guid>();
        var entityByKindPath = new Dictionary<(string kind, string path), Guid>();
        var conflicts = 0;
        var schemaFailures = 0;

        // Sort conventions by priority desc, then by glob specificity desc,
        // then by id asc — see ConflictResolver semantics in the design doc.
        var sorted = conventions
            .OrderByDescending(c => c.Priority)
            .ThenByDescending(c => GlobSpecificity(c.Match.Glob))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // First pass: walk the tree per-convention, but skip files already
        // claimed by a higher-priority convention.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var collectedRefs = new List<PendingRef>();

        foreach (var convention in sorted)
        {
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(convention.Match.Glob);
            foreach (var ex in convention.Match.Exclude)
                matcher.AddExclude(ex);

            var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(workingTreePath));
            var match = matcher.Execute(dirInfo);
            foreach (var fileMatch in match.Files)
            {
                var rel = fileMatch.Path.Replace('\\', '/');
                if (!claimed.Add(rel))
                {
                    conflicts++;
                    continue;
                }
                var fullPath = Path.Combine(workingTreePath, rel);
                if (!File.Exists(fullPath))
                    continue;

                var (entity, refs, schemaOk) = TryProject(
                    workspace,
                    workingTreePath,
                    rel,
                    fullPath,
                    convention,
                    now
                );
                if (entity is null)
                    continue;
                if (!schemaOk)
                    schemaFailures++;

                entities.Add(entity);
                entityById[entity.Id] = entity;
                entityByKindSlug[(entity.Kind, entity.Slug)] = entity.Id;
                entityByKindPath[(entity.Kind, entity.Path)] = entity.Id;
                foreach (var pending in refs)
                    collectedRefs.Add(pending);
            }
        }

        // Second pass: resolve refs against the populated entity index.
        var resolvedRefs = new List<EntityRefProjection>();
        var unresolvedCount = 0;
        var resolvedCount = 0;
        foreach (var pending in collectedRefs)
        {
            Guid? toId = null;
            if (
                pending.SelectKind == "path"
                && entityByKindPath.TryGetValue(
                    (pending.TargetKind ?? string.Empty, pending.TargetPath ?? string.Empty),
                    out var pathId
                )
            )
            {
                toId = pathId;
            }
            else if (
                pending.SelectKind == "frontmatter"
                && entityByKindSlug.TryGetValue(
                    (pending.TargetKind ?? string.Empty, pending.TargetSlug ?? string.Empty),
                    out var slugId
                )
            )
            {
                toId = slugId;
            }

            if (toId is null)
                unresolvedCount++;
            else
                resolvedCount++;

            resolvedRefs.Add(
                new EntityRefProjection(
                    Id: Guid.NewGuid(),
                    WorkspaceId: workspace.Id,
                    FromEntityId: pending.FromEntityId,
                    ToEntityId: toId,
                    Relationship: pending.Relationship,
                    TargetKind: pending.TargetKind,
                    TargetSlug: pending.TargetSlug
                        ?? (
                            pending.SelectKind == "path"
                                ? Path.GetFileNameWithoutExtension(
                                    pending.TargetPath ?? string.Empty
                                )
                                : null
                        ),
                    MetadataJson: null
                )
            );
        }

        sw.Stop();
        var byKind = entities.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        var conventionVersions = sorted.ToDictionary(c => c.Id, c => c.ContentHash);

        var report = new ProjectionReport(
            SyncedAt: now,
            ScanDurationMs: sw.ElapsedMilliseconds,
            ConventionCount: sorted.Count,
            EntitiesByKind: byKind,
            EntityTotal: entities.Count,
            RefsResolved: resolvedCount,
            RefsUnresolved: unresolvedCount,
            SchemaFailures: schemaFailures,
            ConventionConflicts: conflicts,
            ConventionVersions: conventionVersions
        );

        return new ScanResult(entities, resolvedRefs, report);
    }

    private static (
        EntityProjection? Entity,
        IReadOnlyList<PendingRef> Refs,
        bool SchemaOk
    ) TryProject(
        Workspace workspace,
        string workingTreePath,
        string relativePath,
        string fullPath,
        Convention convention,
        DateTime now
    )
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch
        {
            return (null, Array.Empty<PendingRef>(), false);
        }

        var contentHash = Sha256(bytes);
        IReadOnlyDictionary<string, object?>? frontmatter = null;
        var schemaOk = true;

        if (
            string.Equals(
                convention.Metadata.Source,
                "frontmatter",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            frontmatter = TryReadFrontmatter(bytes, relativePath);
            if (frontmatter is null && convention.Match.FrontmatterMustHave.Count > 0)
            {
                // Required frontmatter keys gate the match. Skip silently —
                // the convention's exclude/glob handles "should I match?".
                return (null, Array.Empty<PendingRef>(), false);
            }
            if (frontmatter is not null && convention.Match.FrontmatterMustHave.Count > 0)
            {
                foreach (var key in convention.Match.FrontmatterMustHave)
                {
                    if (!frontmatter.ContainsKey(key))
                        return (null, Array.Empty<PendingRef>(), false);
                }
            }
        }

        if (frontmatter is not null && convention.Metadata.Required.Count > 0)
        {
            foreach (var key in convention.Metadata.Required)
            {
                if (!frontmatter.ContainsKey(key))
                {
                    schemaOk = false;
                    break;
                }
            }
        }

        string slug;
        try
        {
            slug = SlugDeriver.Derive(convention.Slug, relativePath, frontmatter);
        }
        catch (Exception ex)
        {
            // Slug derivation failures are operator-visible by manifesting
            // as missing entities. Surface via schemaFailures to keep the
            // scan moving.
            _ = ex;
            return (null, Array.Empty<PendingRef>(), false);
        }

        var metadataJson = BuildMetadataJson(frontmatter, convention.Metadata.Computed, fullPath);
        var entityId = Guid.NewGuid();
        var entity = new EntityProjection(
            Id: entityId,
            WorkspaceId: workspace.Id,
            Kind: convention.Id,
            Slug: slug,
            Path: relativePath,
            ConventionId: convention.Id,
            MetadataJson: metadataJson,
            ContentHash: contentHash,
            LastSeenAt: now
        );

        var refs = BuildRefs(entity, convention, relativePath, frontmatter);
        return (entity, refs, schemaOk);
    }

    private static IReadOnlyList<PendingRef> BuildRefs(
        EntityProjection from,
        Convention convention,
        string relativePath,
        IReadOnlyDictionary<string, object?>? frontmatter
    )
    {
        var pending = new List<PendingRef>();
        var fileDir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        var parentDir = string.IsNullOrEmpty(fileDir) ? string.Empty : Path.GetFileName(fileDir);

        foreach (var rel in convention.Relationships)
        {
            if (!string.IsNullOrWhiteSpace(rel.SelectPath))
            {
                var resolved = rel
                    .SelectPath.Replace("{file_dir}", fileDir, StringComparison.Ordinal)
                    .Replace("{parent_dir}", parentDir, StringComparison.Ordinal);
                pending.Add(
                    new PendingRef(
                        FromEntityId: from.Id,
                        Relationship: rel.Kind,
                        TargetKind: rel.TargetKind,
                        TargetSlug: null,
                        TargetPath: resolved,
                        SelectKind: "path"
                    )
                );
            }
            else if (
                !string.IsNullOrWhiteSpace(rel.SelectFrontmatter)
                && frontmatter is not null
                && frontmatter.TryGetValue(rel.SelectFrontmatter, out var fmValue)
            )
            {
                foreach (var slug in EnumerateRefValues(fmValue))
                {
                    pending.Add(
                        new PendingRef(
                            FromEntityId: from.Id,
                            Relationship: rel.Kind,
                            TargetKind: rel.TargetKind,
                            TargetSlug: slug,
                            TargetPath: null,
                            SelectKind: "frontmatter"
                        )
                    );
                }
            }
        }
        return pending;
    }

    private static IEnumerable<string> EnumerateRefValues(object? value)
    {
        if (value is null)
            yield break;
        if (value is string s)
        {
            if (!string.IsNullOrWhiteSpace(s))
                yield return s.Trim();
            yield break;
        }
        if (value is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                if (item is null)
                    continue;
                var t = item.ToString();
                if (!string.IsNullOrWhiteSpace(t))
                    yield return t.Trim();
            }
        }
        else
        {
            // Single non-string scalar (number, bool); treat as one ref.
            var t = value.ToString();
            if (!string.IsNullOrWhiteSpace(t))
                yield return t.Trim();
        }
    }

    private static IReadOnlyDictionary<string, object?>? TryReadFrontmatter(
        byte[] bytes,
        string relativePath
    )
    {
        var dialect = FrontmatterDialects.FromPath(relativePath);
        if (dialect is null)
            return null;
        try
        {
            var content = Encoding.UTF8.GetString(bytes);
            var found = FrontmatterIO.Find(content, dialect);
            if (!found.Existed)
                return null;
            return FrontmatterIO.ParsePayload(found.YamlPayload);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildMetadataJson(
        IReadOnlyDictionary<string, object?>? frontmatter,
        IReadOnlyDictionary<string, string> computed,
        string fullPath
    )
    {
        var merged = new Dictionary<string, object?>();
        if (frontmatter is not null)
        {
            foreach (var (k, v) in frontmatter)
                merged[k] = v;
        }
        foreach (var (key, accessor) in computed)
        {
            try
            {
                var value = ResolveComputed(accessor, fullPath);
                if (value is not null)
                    merged[key] = value;
            }
            catch
            {
                // best effort
            }
        }
        return JsonSerializer.Serialize(merged);
    }

    private static object? ResolveComputed(string accessor, string fullPath) =>
        accessor switch
        {
            "file.line_count" => CountLines(fullPath),
            "file.mtime" => File.GetLastWriteTimeUtc(fullPath).ToString("O"),
            "file.size" => new FileInfo(fullPath).Length,
            // git.* accessors land alongside the IWorkspaceWorkingTree shell-out
            // path in v0.2 — recording the contract here so consumers can
            // expect the namespace.
            _ => null,
        };

    private static int CountLines(string fullPath)
    {
        var n = 0;
        using var reader = new StreamReader(fullPath);
        while (reader.ReadLine() is not null)
            n++;
        return n;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Cheap glob-specificity heuristic: longer literal prefix + more
    /// segments wins. Good enough for the conflict-resolution tiebreaker;
    /// matches operator intuition.
    /// </summary>
    private static int GlobSpecificity(string glob)
    {
        var literal = glob.Length;
        var stars = 0;
        foreach (var ch in glob)
            if (ch == '*' || ch == '?')
                stars++;
        return literal - (stars * 4);
    }

    /// <summary>
    /// Internal staging shape for pre-resolved refs. The first pass collects
    /// these; the second pass resolves them against the populated entity index.
    /// </summary>
    private sealed record PendingRef(
        Guid FromEntityId,
        string Relationship,
        string? TargetKind,
        string? TargetSlug,
        string? TargetPath,
        string SelectKind
    );

    public sealed record ScanResult(
        IReadOnlyList<EntityProjection> Entities,
        IReadOnlyList<EntityRefProjection> Refs,
        ProjectionReport Report
    );
}
