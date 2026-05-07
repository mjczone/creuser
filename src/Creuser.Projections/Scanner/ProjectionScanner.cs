using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Accessors;
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
    private readonly ComputedAccessorRegistry _accessors;

    public ProjectionScanner(TimeProvider time)
        : this(time, ComputedAccessorRegistry.Default) { }

    public ProjectionScanner(TimeProvider time, ComputedAccessorRegistry accessors)
    {
        _time = time;
        _accessors = accessors;
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

        // Build a kind-agnostic path index too, for target_kind: any path lookups.
        var entityByPath = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var entityBySlug = new Dictionary<string, List<(string Kind, Guid Id)>>(
            StringComparer.Ordinal
        );
        foreach (var e in entities)
        {
            entityByPath[e.Path] = e.Id;
            if (!entityBySlug.TryGetValue(e.Slug, out var list))
            {
                list = new List<(string, Guid)>();
                entityBySlug[e.Slug] = list;
            }
            list.Add((e.Kind, e.Id));
        }

        // Second pass: resolve pending refs against the entity index.
        var resolvedRefs = new List<EntityRefProjection>();
        var unresolvedCount = 0;
        var resolvedCount = 0;
        foreach (var pending in collectedRefs)
        {
            var resolution = ResolvePending(
                pending,
                entityByKindSlug,
                entityByKindPath,
                entityByPath,
                entityBySlug
            );
            if (resolution.ToId is null)
                unresolvedCount++;
            else
                resolvedCount++;

            resolvedRefs.Add(
                new EntityRefProjection(
                    Id: Guid.NewGuid(),
                    WorkspaceId: workspace.Id,
                    FromEntityId: pending.FromEntityId,
                    ToEntityId: resolution.ToId,
                    Relationship: pending.Relationship,
                    TargetKind: resolution.TargetKind,
                    TargetSlug: resolution.TargetSlug,
                    MetadataJson: BuildRefMetadataJson(pending, resolution)
                )
            );
        }

        // Third pass: emit inverse edges. For every pending rule that declared an
        // `inverse:`, mirror the resolved edges so the CDFS shows the relationship
        // from the target's side without the author duplicating frontmatter.
        var inverseRefs = new List<EntityRefProjection>();
        foreach (var pending in collectedRefs)
        {
            if (string.IsNullOrWhiteSpace(pending.InverseKind))
                continue;
            var resolution = ResolvePending(
                pending,
                entityByKindSlug,
                entityByKindPath,
                entityByPath,
                entityBySlug
            );
            if (resolution.ToId is null)
                continue; // can't reverse an edge whose forward target didn't resolve to an entity
            inverseRefs.Add(
                new EntityRefProjection(
                    Id: Guid.NewGuid(),
                    WorkspaceId: workspace.Id,
                    FromEntityId: resolution.ToId.Value,
                    ToEntityId: pending.FromEntityId,
                    Relationship: pending.InverseKind!,
                    TargetKind: pending.FromEntityKind,
                    TargetSlug: pending.FromEntitySlug,
                    MetadataJson: BuildInverseMetadataJson(pending)
                )
            );
            resolvedCount++;
        }
        resolvedRefs.AddRange(inverseRefs);

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

    private (EntityProjection? Entity, IReadOnlyList<PendingRef> Refs, bool SchemaOk) TryProject(
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

        var metadataJson = BuildMetadataJson(
            frontmatter,
            convention.Metadata.Computed,
            fullPath,
            relativePath,
            bytes
        );
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

        var refs = BuildPendingRefs(
            entity,
            convention,
            relativePath,
            frontmatter,
            bytes,
            workingTreePath
        );
        return (entity, refs, schemaOk);
    }

    /// <summary>
    /// First-pass ref builder: walks the convention's relationship rules, yields
    /// source values per rule, applies the per-rule filter, classifies each value
    /// (URL / glob / path / slug) into a <see cref="PendingRef"/>, and expands
    /// globs against the working tree. Resolution itself happens in the second
    /// pass once all entities are indexed.
    /// </summary>
    private static IReadOnlyList<PendingRef> BuildPendingRefs(
        EntityProjection from,
        Convention convention,
        string relativePath,
        IReadOnlyDictionary<string, object?>? frontmatter,
        byte[] bytes,
        string workingTreePath
    )
    {
        var pending = new List<PendingRef>();
        var fileDir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        var parentDir = string.IsNullOrEmpty(fileDir) ? string.Empty : Path.GetFileName(fileDir);

        foreach (var rel in convention.Relationships)
        {
            var via = DescribeSourceVia(rel.Source);
            foreach (
                var raw in EnumerateSourceValues(rel.Source, frontmatter, bytes, fileDir, parentDir)
            )
            {
                if (raw is null)
                    continue;
                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                    continue;

                var classified = Classify(trimmed, rel.Interpret);

                if (rel.Filter is not null && !MatchesFilter(trimmed, classified, rel.Filter))
                    continue;

                if (classified == PendingRefShape.Glob)
                {
                    foreach (var globMatch in ExpandGlob(workingTreePath, trimmed))
                    {
                        pending.Add(
                            BuildPath(
                                from,
                                rel,
                                globMatch,
                                raw: trimmed,
                                via: $"glob:{trimmed}",
                                expandedFrom: trimmed
                            )
                        );
                    }
                    continue;
                }

                pending.Add(
                    classified switch
                    {
                        PendingRefShape.Url => BuildUrl(from, rel, trimmed, via),
                        PendingRefShape.EntityByPath => BuildPath(
                            from,
                            rel,
                            trimmed,
                            raw: trimmed,
                            via,
                            expandedFrom: null
                        ),
                        PendingRefShape.EntityBySlug => BuildSlug(from, rel, trimmed, via),
                        _ => BuildSlug(from, rel, trimmed, via),
                    }
                );
            }
        }
        return pending;
    }

    private static PendingRef BuildPath(
        EntityProjection from,
        ConventionRelationship rel,
        string path,
        string raw,
        string? via,
        string? expandedFrom
    ) =>
        new(
            FromEntityId: from.Id,
            FromEntityKind: from.Kind,
            FromEntitySlug: from.Slug,
            Relationship: rel.Kind,
            Shape: PendingRefShape.EntityByPath,
            TargetKindFilter: rel.TargetKind,
            Raw: raw,
            Path: path,
            Slug: null,
            Url: null,
            Via: via,
            ExpandedFrom: expandedFrom,
            UserMetadata: rel.Metadata,
            InverseKind: rel.Inverse
        );

    private static PendingRef BuildSlug(
        EntityProjection from,
        ConventionRelationship rel,
        string slug,
        string? via
    ) =>
        new(
            FromEntityId: from.Id,
            FromEntityKind: from.Kind,
            FromEntitySlug: from.Slug,
            Relationship: rel.Kind,
            Shape: PendingRefShape.EntityBySlug,
            TargetKindFilter: rel.TargetKind,
            Raw: slug,
            Path: null,
            Slug: slug,
            Url: null,
            Via: via,
            ExpandedFrom: null,
            UserMetadata: rel.Metadata,
            InverseKind: rel.Inverse
        );

    private static PendingRef BuildUrl(
        EntityProjection from,
        ConventionRelationship rel,
        string url,
        string? via
    ) =>
        new(
            FromEntityId: from.Id,
            FromEntityKind: from.Kind,
            FromEntitySlug: from.Slug,
            Relationship: rel.Kind,
            Shape: PendingRefShape.Url,
            TargetKindFilter: rel.TargetKind,
            Raw: url,
            Path: null,
            Slug: null,
            Url: url,
            Via: via,
            ExpandedFrom: null,
            UserMetadata: rel.Metadata,
            InverseKind: null // never reverse a URL — there's no entity on the other side
        );

    /// <summary>
    /// Yield raw string values from a rule's source. Frontmatter lists yield
    /// each item; path-template yields the (interpolated) path; glob yields
    /// the glob pattern itself (the expander applies later); literal yields
    /// each declared literal. body-* sources are placeholders for Stage F.
    /// </summary>
    private static IEnumerable<string?> EnumerateSourceValues(
        ConventionRefSource source,
        IReadOnlyDictionary<string, object?>? frontmatter,
        byte[] bytes,
        string fileDir,
        string parentDir
    )
    {
        switch (source.Kind)
        {
            case "frontmatter":
                if (
                    frontmatter is null
                    || string.IsNullOrWhiteSpace(source.Key)
                    || !frontmatter.TryGetValue(source.Key, out var fmValue)
                )
                    yield break;
                foreach (var v in YamlScalars(fmValue))
                    yield return v;
                yield break;
            case "path-template":
                if (string.IsNullOrWhiteSpace(source.Key))
                    yield break;
                yield return source
                    .Key.Replace("{file_dir}", fileDir, StringComparison.Ordinal)
                    .Replace("{parent_dir}", parentDir, StringComparison.Ordinal);
                yield break;
            case "glob":
                // The pattern is the value; the resolver classifies it as a glob
                // and expands. Carries through Auto interpretation paths too.
                if (!string.IsNullOrWhiteSpace(source.Key))
                    yield return source.Key;
                yield break;
            case "literal":
                if (source.Literals is not null)
                    foreach (var v in source.Literals)
                        yield return v;
                yield break;
            case "body-links":
                foreach (var v in ExtractBodyLinks(bytes))
                    yield return v;
                yield break;
            case "body-code-refs":
                foreach (var v in ExtractBodyCodeRefs(bytes))
                    yield return v;
                yield break;
            default:
                yield break;
        }
    }

    /// <summary>
    /// Extract markdown links from the file body (frontmatter stripped).
    /// Yields the URL portion of each <c>[text](url)</c>; the resolver's
    /// auto-classifier turns each into the right shape (URL / path / slug).
    /// Reference-style links (<c>[label]: url</c>) match too.
    /// </summary>
    private static IEnumerable<string> ExtractBodyLinks(byte[] bytes)
    {
        var body = StripFrontmatter(SafeUtf8(bytes));
        if (string.IsNullOrEmpty(body))
            yield break;
        foreach (Match m in MarkdownInlineLink.Matches(body))
            yield return m.Groups[1].Value.Trim();
        foreach (Match m in MarkdownReferenceLink.Matches(body))
            yield return m.Groups[1].Value.Trim();
    }

    /// <summary>
    /// Extract code-reference paths from the body — inline-code spans whose
    /// content looks like a workspace-relative file path. Catches patterns
    /// like <c>`src/Foo.cs`</c>, <c>`packages/db/repo.ts:42`</c>. Less
    /// aggressive than markdown-link extraction so prose doesn't blow up
    /// the ref count.
    /// </summary>
    private static IEnumerable<string> ExtractBodyCodeRefs(byte[] bytes)
    {
        var body = StripFrontmatter(SafeUtf8(bytes));
        if (string.IsNullOrEmpty(body))
            yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in InlineCodeRef.Matches(body))
        {
            var raw = m.Groups[1].Value.Trim();
            // Strip a `:line` suffix if present — the resolver works on paths,
            // and the line number is preserved on the unresolved metadata side
            // by the raw value embed.
            var colon = raw.IndexOf(':');
            var path = colon > 0 && raw[(colon + 1)..].All(char.IsDigit) ? raw[..colon] : raw;
            if (seen.Add(path))
                yield return path;
        }
    }

    private static string StripFrontmatter(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (!text.StartsWith("---", StringComparison.Ordinal))
            return text;
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end <= 0)
            return text;
        var after = end + 4;
        if (after < text.Length && text[after] == '\n')
            after++;
        return after >= text.Length ? string.Empty : text[after..];
    }

    private static string SafeUtf8(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    // [text](url) — naive but good enough for body extractors. Excludes
    // images (those have a `!` prefix outside the alt text); image-style
    // (`![](src)`) won't match here.
    private static readonly Regex MarkdownInlineLink = new(
        @"(?<!\!)\[(?:[^\]]*)\]\(([^)\s]+)\)",
        RegexOptions.Compiled
    );

    // [label]: url — reference-style link definitions, one per line.
    private static readonly Regex MarkdownReferenceLink = new(
        @"^\s*\[[^\]]+\]:\s+(\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline
    );

    // `path/to/file.ext` or `path/to/file.ext:42`. Requires a `/` plus a
    // dotted file extension to avoid matching arbitrary inline code like
    // <c>`foo`</c> or <c>`x.y.z`</c> (no slashes → not a path).
    private static readonly Regex InlineCodeRef = new(
        @"`([\w\-./]+/[\w\-]+\.[\w]{1,8}(?::\d+)?)`",
        RegexOptions.Compiled
    );

    private static IEnumerable<string?> YamlScalars(object? value)
    {
        if (value is null)
            yield break;
        if (value is string s)
        {
            yield return s;
            yield break;
        }
        if (value is IEnumerable<object?> list)
        {
            foreach (var item in list)
            {
                if (item is null)
                    continue;
                yield return item.ToString();
            }
            yield break;
        }
        yield return value.ToString();
    }

    private static string? DescribeSourceVia(ConventionRefSource source) =>
        source.Kind switch
        {
            "frontmatter" => $"frontmatter.{source.Key}",
            "path-template" => $"path-template:{source.Key}",
            "glob" => $"glob:{source.Key}",
            "literal" => "literal",
            _ => source.Kind,
        };

    /// <summary>
    /// Decide what shape a yielded value should resolve as. <c>auto</c> sniffs
    /// (URL → glob → path → slug); the other modes force an interpretation.
    /// </summary>
    private static PendingRefShape Classify(string value, ConventionRefInterpret interpret)
    {
        return interpret switch
        {
            ConventionRefInterpret.Url => PendingRefShape.Url,
            ConventionRefInterpret.Glob => PendingRefShape.Glob,
            ConventionRefInterpret.Path => PendingRefShape.EntityByPath,
            ConventionRefInterpret.Slug => PendingRefShape.EntityBySlug,
            ConventionRefInterpret.RefObject => PendingRefShape.EntityBySlug, // structured object support deferred
            _ => SniffAuto(value),
        };
    }

    private static PendingRefShape SniffAuto(string value)
    {
        if (
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
            return PendingRefShape.Url;
        if (LooksLikeGlob(value))
            return PendingRefShape.Glob;
        if (LooksLikePath(value))
            return PendingRefShape.EntityByPath;
        return PendingRefShape.EntityBySlug;
    }

    private static bool LooksLikeGlob(string s)
    {
        foreach (var ch in s)
            if (ch == '*' || ch == '?' || ch == '[')
                return true;
        return false;
    }

    private static bool LooksLikePath(string s)
    {
        if (s.Contains('/'))
            return true;
        var ext = Path.GetExtension(s);
        return ext.Length >= 2;
    }

    private static bool MatchesFilter(
        string value,
        PendingRefShape shape,
        ConventionRefFilter filter
    )
    {
        switch (filter.Kind)
        {
            case "glob":
            {
                var pattern = filter.Pattern;
                var negate = pattern.StartsWith('!');
                if (negate)
                    pattern = pattern[1..];
                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(pattern);
                var pseudoRoot = "/";
                var match = matcher.Match(pseudoRoot, value.StartsWith('/') ? value : "/" + value);
                var hit = match.HasMatches;
                return negate ? !hit : hit;
            }
            case "regex":
                try
                {
                    return Regex.IsMatch(value, filter.Pattern);
                }
                catch
                {
                    return false;
                }
            case "type":
            {
                var typeName = shape switch
                {
                    PendingRefShape.Url => "url",
                    PendingRefShape.Glob => "glob",
                    PendingRefShape.EntityByPath => "path",
                    PendingRefShape.EntityBySlug => "slug",
                    _ => "slug",
                };
                return string.Equals(
                    typeName,
                    filter.Pattern.Trim(),
                    StringComparison.OrdinalIgnoreCase
                );
            }
            default:
                return true;
        }
    }

    private static IEnumerable<string> ExpandGlob(string workingTreePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(workingTreePath) || !Directory.Exists(workingTreePath))
            yield break;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);
        var dir = new DirectoryInfoWrapper(new DirectoryInfo(workingTreePath));
        var match = matcher.Execute(dir);
        foreach (var file in match.Files)
            yield return file.Path.Replace('\\', '/');
    }

    /// <summary>
    /// Resolve one pending ref against the entity index. Produces the resolved
    /// entity id (or null if unresolved), plus the canonical <c>target_kind</c>
    /// / <c>target_slug</c> values to write back into <c>entity_refs</c>.
    /// </summary>
    private static (Guid? ToId, string? TargetKind, string? TargetSlug) ResolvePending(
        PendingRef pending,
        Dictionary<(string kind, string slug), Guid> byKindSlug,
        Dictionary<(string kind, string path), Guid> byKindPath,
        Dictionary<string, Guid> byPath,
        Dictionary<string, List<(string Kind, Guid Id)>> bySlug
    )
    {
        switch (pending.Shape)
        {
            case PendingRefShape.EntityBySlug:
            {
                var slug = pending.Slug ?? string.Empty;
                if (pending.TargetKindFilter.Any)
                {
                    if (bySlug.TryGetValue(slug, out var entries) && entries.Count > 0)
                        return (entries[0].Id, entries[0].Kind, slug);
                    return (null, null, slug);
                }
                foreach (var k in pending.TargetKindFilter.Allowed)
                {
                    if (byKindSlug.TryGetValue((k, slug), out var id))
                        return (id, k, slug);
                }
                return (null, pending.TargetKindFilter.Allowed.FirstOrDefault(), slug);
            }
            case PendingRefShape.EntityByPath:
            {
                var path = pending.Path ?? string.Empty;
                if (pending.TargetKindFilter.Any)
                {
                    if (byPath.TryGetValue(path, out var id))
                    {
                        // Walk byKindPath to recover the kind for this entity.
                        // O(N) over the kind dimension — N is the number of
                        // kinds in the workspace, which is small.
                        foreach (var ((k, p), kid) in byKindPath)
                            if (kid == id && p == path)
                                return (kid, k, Path.GetFileNameWithoutExtension(path));
                    }
                    return (null, null, Path.GetFileNameWithoutExtension(path));
                }
                foreach (var k in pending.TargetKindFilter.Allowed)
                {
                    if (byKindPath.TryGetValue((k, path), out var id))
                        return (id, k, Path.GetFileNameWithoutExtension(path));
                }
                return (
                    null,
                    pending.TargetKindFilter.Allowed.FirstOrDefault(),
                    Path.GetFileNameWithoutExtension(path)
                );
            }
            case PendingRefShape.Url:
                return (null, null, null);
            default:
                return (null, null, null);
        }
    }

    private static string BuildRefMetadataJson(
        PendingRef pending,
        (Guid? ToId, string? TargetKind, string? TargetSlug) resolution
    )
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        string kind;
        if (pending.Shape == PendingRefShape.Url)
            kind = "url";
        else if (resolution.ToId is not null)
            kind = "entity";
        else
            kind = pending.Shape == PendingRefShape.EntityByPath ? "file" : "slug";
        meta["kind"] = kind;
        if (!string.IsNullOrWhiteSpace(pending.Via))
            meta["via"] = pending.Via;
        if (!string.IsNullOrWhiteSpace(pending.Raw))
            meta["raw"] = pending.Raw;
        if (!string.IsNullOrWhiteSpace(pending.ExpandedFrom))
            meta["expanded_from"] = pending.ExpandedFrom;
        if (pending.Url is not null)
            meta["url"] = pending.Url;
        if (pending.Path is not null && kind != "entity")
            meta["path"] = pending.Path;
        if (pending.UserMetadata is not null)
        {
            foreach (var (k, v) in pending.UserMetadata)
                meta[k] = ExpandMetadataTemplate(v, pending);
        }
        return JsonSerializer.Serialize(meta);
    }

    private static string BuildInverseMetadataJson(PendingRef pending)
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "entity",
            ["inverse_of"] = pending.Relationship,
        };
        if (!string.IsNullOrWhiteSpace(pending.Via))
            meta["via"] = pending.Via;
        return JsonSerializer.Serialize(meta);
    }

    /// <summary>
    /// Tiny <c>${value}</c> placeholder substitution for per-edge metadata
    /// templates. v1 supports just <c>${value}</c>; richer variables land
    /// alongside the structured ops endpoint.
    /// </summary>
    private static string ExpandMetadataTemplate(string template, PendingRef pending) =>
        template.Replace("${value}", pending.Raw, StringComparison.Ordinal);

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

    private string BuildMetadataJson(
        IReadOnlyDictionary<string, object?>? frontmatter,
        IReadOnlyDictionary<string, string> computed,
        string fullPath,
        string relativePath,
        byte[] bytes
    )
    {
        var merged = new Dictionary<string, object?>();
        if (frontmatter is not null)
        {
            foreach (var (k, v) in frontmatter)
                merged[k] = v;
        }
        if (computed.Count == 0)
            return JsonSerializer.Serialize(merged);

        var ctx = new AccessorContext(
            FullPath: fullPath,
            RelativePath: relativePath,
            Frontmatter: frontmatter,
            ReadBytes: () => bytes
        );
        foreach (var (key, accessor) in computed)
        {
            if (!_accessors.TryGet(accessor, out var field))
                continue; // unknown accessor: scanner stays silent; schema validation surfaces it
            try
            {
                var value = field.Resolve(ctx);
                if (value is not null)
                    merged[key] = value;
            }
            catch
            {
                // best effort — accessor failures don't block the scan
            }
        }
        return JsonSerializer.Serialize(merged);
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
    /// Internal staging shape for pre-classified refs. The first pass collects
    /// these (one per yielded source value, after filter + classification + glob
    /// expansion); the second pass resolves them against the populated entity
    /// index; the third pass walks them to emit reverse edges.
    /// </summary>
    private sealed record PendingRef(
        Guid FromEntityId,
        string FromEntityKind,
        string FromEntitySlug,
        string Relationship,
        PendingRefShape Shape,
        ConventionRefTargetKind TargetKindFilter,
        string Raw,
        string? Path,
        string? Slug,
        string? Url,
        string? Via,
        string? ExpandedFrom,
        IReadOnlyDictionary<string, string>? UserMetadata,
        string? InverseKind
    );

    /// <summary>
    /// Classification of a yielded source value. Drives both the second-pass
    /// resolver (which lookup table to probe) and the metadata envelope kind.
    /// </summary>
    private enum PendingRefShape
    {
        EntityBySlug,
        EntityByPath,
        Url,
        Glob, // transient — expanded into per-match EntityByPath refs in the first pass
    }

    public sealed record ScanResult(
        IReadOnlyList<EntityProjection> Entities,
        IReadOnlyList<EntityRefProjection> Refs,
        ProjectionReport Report
    );
}
