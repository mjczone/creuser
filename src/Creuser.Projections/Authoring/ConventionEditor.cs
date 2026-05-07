using System.Text;
using Creuser.Core.Execution;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Projections.Conventions;
using Creuser.Projections.Scanner;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Creuser.Projections.Authoring;

/// <summary>
/// Structured-edit operations for workspace conventions. Reads a workspace's
/// <c>.creuser/conventions/*.yaml</c>, mutates the relationship rules
/// declaratively, validates the result against the loader/schema, and writes
/// back to disk.
///
/// <para>
/// All edits round-trip via <see cref="YamlStream"/> against the YAML
/// representation model — node ordering is preserved when fields are
/// untouched, and only the affected sub-tree is rewritten. Inline comments
/// are <em>not</em> preserved (a YamlDotNet limitation). Authors who care
/// about comments should keep them out of mutated regions, or hand-edit.
/// </para>
///
/// <para>
/// The editor's API is the surface the AI assistant + CLI both call. Each
/// op runs the loader against the resulting YAML before writing — a malformed
/// edit returns the validation errors instead of corrupting the file.
/// </para>
/// </summary>
public sealed class ConventionEditor
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private readonly IWorkspaceWorkingTree _tree;

    public ConventionEditor(IWorkspaceWorkingTree tree)
    {
        _tree = tree;
    }

    /// <summary>
    /// Add a new relationship rule to a convention. Fails when a rule with
    /// the same kind already exists; use <see cref="UpdateRelationshipAsync"/>
    /// for that case.
    /// </summary>
    public async Task<EditResult> AddRelationshipAsync(
        Workspace workspace,
        string conventionId,
        RelationshipEdit edit,
        CancellationToken ct = default
    )
    {
        var (path, yaml) = await ReadConventionFileAsync(workspace, conventionId, ct);
        var (root, relationships) = ParseAndLocateRelationships(yaml, createIfMissing: true);

        if (FindRelationshipNode(relationships, edit.Kind) is not null)
        {
            return EditResult.Failed(
                $"Relationship '{edit.Kind}' already exists on convention '{conventionId}'. Use update_relationship to modify."
            );
        }

        relationships.Add(BuildRelationshipMapping(edit));
        var rewritten = SerializeRoot(root);
        return await ValidateAndWriteAsync(workspace, conventionId, path, rewritten, ct);
    }

    public async Task<EditResult> UpdateRelationshipAsync(
        Workspace workspace,
        string conventionId,
        string kind,
        RelationshipEdit edit,
        CancellationToken ct = default
    )
    {
        var (path, yaml) = await ReadConventionFileAsync(workspace, conventionId, ct);
        var (root, relationships) = ParseAndLocateRelationships(yaml, createIfMissing: false);

        var index = FindRelationshipIndex(relationships, kind);
        if (index < 0)
        {
            return EditResult.Failed(
                $"Relationship '{kind}' not found on convention '{conventionId}'."
            );
        }

        var canonicalKind = string.IsNullOrWhiteSpace(edit.Kind) ? kind : edit.Kind;
        var withKind = edit with { Kind = canonicalKind };
        relationships.Children[index] = BuildRelationshipMapping(withKind);
        var rewritten = SerializeRoot(root);
        return await ValidateAndWriteAsync(workspace, conventionId, path, rewritten, ct);
    }

    public async Task<EditResult> RemoveRelationshipAsync(
        Workspace workspace,
        string conventionId,
        string kind,
        CancellationToken ct = default
    )
    {
        var (path, yaml) = await ReadConventionFileAsync(workspace, conventionId, ct);
        var (root, relationships) = ParseAndLocateRelationships(yaml, createIfMissing: false);

        var index = FindRelationshipIndex(relationships, kind);
        if (index < 0)
        {
            return EditResult.Failed(
                $"Relationship '{kind}' not found on convention '{conventionId}'."
            );
        }
        relationships.Children.RemoveAt(index);
        var rewritten = SerializeRoot(root);
        return await ValidateAndWriteAsync(workspace, conventionId, path, rewritten, ct);
    }

    /// <summary>
    /// Parse + validate YAML without touching the filesystem. Used by the
    /// validate endpoint and by every mutating op as a pre-write gate.
    /// </summary>
    public ValidationResult Validate(string yaml, string? sourcePath = null)
    {
        var (convention, error) = ConventionLoader.Parse(yaml, sourcePath);
        var errors = error is null ? Array.Empty<ConventionLoadError>() : new[] { error };
        return new ValidationResult(convention, errors);
    }

    /// <summary>
    /// Dry-run scan: run the projection scanner with a single convention
    /// against the workspace and return the entity + refs that would be
    /// produced for the given path. The result includes globally-resolved
    /// refs since the scanner sees the whole workspace.
    /// </summary>
    public async Task<TestResult> TestAsync(
        Workspace workspace,
        string conventionId,
        string againstPath,
        IConventionLoader loader,
        ProjectionScanner scanner,
        CancellationToken ct = default
    )
    {
        var workingPath = await _tree.ResolvePathAsync(workspace, ct);
        if (string.IsNullOrEmpty(workingPath) || !Directory.Exists(workingPath))
            return TestResult.Failed("Working tree is unavailable. Sync the workspace first.");

        var loaded = await loader.LoadAsync(workspace, workingPath, ct);
        var convention = loaded.Conventions.FirstOrDefault(c =>
            string.Equals(c.Id, conventionId, StringComparison.Ordinal)
        );
        if (convention is null)
            return TestResult.Failed($"Convention '{conventionId}' was not loaded.");

        var scan = scanner.Scan(workspace, workingPath, new[] { convention });
        var normalized = againstPath.Replace('\\', '/');
        var matched = scan.Entities.FirstOrDefault(e =>
            string.Equals(e.Path, normalized, StringComparison.Ordinal)
        );

        if (matched is null)
        {
            return new TestResult(
                Matched: false,
                Entity: null,
                Refs: Array.Empty<EntityRefProjection>(),
                Report: scan.Report,
                Error: $"'{normalized}' was not matched by convention '{conventionId}'."
            );
        }

        var refs = scan.Refs.Where(r => r.FromEntityId == matched.Id).ToList();
        return new TestResult(
            Matched: true,
            Entity: matched,
            Refs: refs,
            Report: scan.Report,
            Error: null
        );
    }

    // ---------- internals ----------

    private async Task<(string FullPath, string Yaml)> ReadConventionFileAsync(
        Workspace workspace,
        string conventionId,
        CancellationToken ct
    )
    {
        var workingPath = await _tree.ResolvePathAsync(workspace, ct);
        if (string.IsNullOrEmpty(workingPath) || !Directory.Exists(workingPath))
            throw new InvalidOperationException("Working tree is unavailable.");
        var conventionsDir = Path.Combine(workingPath, ".creuser", "conventions");
        if (!Directory.Exists(conventionsDir))
            throw new InvalidOperationException(
                ".creuser/conventions directory does not exist in the working tree."
            );

        // Linear scan — convention dirs are small (single-tenant; usually <50
        // files). Each file's `id:` field is the canonical key.
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
        )
        {
            var content = await File.ReadAllTextAsync(path, ct);
            var (conv, _) = ConventionLoader.Parse(content, sourcePath: null);
            if (conv is not null && string.Equals(conv.Id, conventionId, StringComparison.Ordinal))
                return (path, content);
        }

        throw new InvalidOperationException($"No convention file declares id '{conventionId}'.");
    }

    private static (
        YamlMappingNode Root,
        YamlSequenceNode Relationships
    ) ParseAndLocateRelationships(string yaml, bool createIfMissing)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            throw new InvalidOperationException("Convention YAML is empty.");

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var key = new YamlScalarNode("relationships");
        if (root.Children.TryGetValue(key, out var node) && node is YamlSequenceNode seq)
            return (root, seq);

        if (!createIfMissing)
        {
            // Empty seq for callers — they'll see "kind not found" the same way.
            var empty = new YamlSequenceNode();
            root.Children.Add(key, empty);
            return (root, empty);
        }

        var newSeq = new YamlSequenceNode();
        root.Children.Add(key, newSeq);
        return (root, newSeq);
    }

    private static int FindRelationshipIndex(YamlSequenceNode relationships, string kind)
    {
        for (var i = 0; i < relationships.Children.Count; i++)
        {
            if (
                relationships.Children[i] is YamlMappingNode m
                && m.Children.TryGetValue(new YamlScalarNode("kind"), out var k)
                && k is YamlScalarNode ks
                && string.Equals(ks.Value, kind, StringComparison.Ordinal)
            )
                return i;
        }
        return -1;
    }

    private static YamlMappingNode? FindRelationshipNode(
        YamlSequenceNode relationships,
        string kind
    )
    {
        var idx = FindRelationshipIndex(relationships, kind);
        return idx < 0 ? null : (YamlMappingNode)relationships.Children[idx];
    }

    /// <summary>
    /// Translate a structured <see cref="RelationshipEdit"/> into a YAML
    /// mapping. Field order is intentional: identity → display → resolution
    /// → inverse, mirroring how authors think about a rule.
    /// </summary>
    private static YamlMappingNode BuildRelationshipMapping(RelationshipEdit edit)
    {
        var node = new YamlMappingNode();
        AddScalar(node, "kind", edit.Kind);
        if (edit.Name is not null)
            AddScalar(node, "name", edit.Name);
        if (edit.Icon is not null)
            AddScalar(node, "icon", edit.Icon);
        if (edit.Description is not null)
            AddScalar(node, "description", edit.Description);
        if (edit.Order is not null)
            AddScalar(node, "order", edit.Order.Value.ToString());
        if (edit.Source is not null)
            node.Children.Add(new YamlScalarNode("source"), BuildScalarOrMap(edit.Source));
        if (edit.Filter is not null)
            node.Children.Add(new YamlScalarNode("filter"), BuildScalarOrMap(edit.Filter));
        if (edit.Interpret is not null)
            AddScalar(node, "interpret", edit.Interpret);
        if (edit.TargetKind is not null)
            node.Children.Add(
                new YamlScalarNode("target_kind"),
                BuildScalarOrSeqOrMap(edit.TargetKind)
            );
        if (edit.Inverse is not null)
            AddScalar(node, "inverse", edit.Inverse);
        if (edit.InverseName is not null)
            AddScalar(node, "inverse_name", edit.InverseName);
        if (edit.InverseIcon is not null)
            AddScalar(node, "inverse_icon", edit.InverseIcon);
        if (edit.Metadata is not null && edit.Metadata.Count > 0)
        {
            var meta = new YamlMappingNode();
            foreach (var (k, v) in edit.Metadata)
                AddScalar(meta, k, v);
            node.Children.Add(new YamlScalarNode("metadata"), meta);
        }
        return node;
    }

    private static void AddScalar(YamlMappingNode node, string key, string value) =>
        node.Children.Add(new YamlScalarNode(key), new YamlScalarNode(value));

    private static YamlNode BuildScalarOrMap(object value)
    {
        if (value is string s)
            return new YamlScalarNode(s);
        if (value is IReadOnlyDictionary<string, object?> dict)
        {
            var map = new YamlMappingNode();
            foreach (var (k, v) in dict)
            {
                if (v is null)
                    continue;
                if (v is string vs)
                    AddScalar(map, k, vs);
                else if (v is IEnumerable<string> seq)
                {
                    var items = new YamlSequenceNode(
                        seq.Select(x => (YamlNode)new YamlScalarNode(x))
                    );
                    map.Children.Add(new YamlScalarNode(k), items);
                }
                else
                    AddScalar(map, k, v.ToString() ?? string.Empty);
            }
            return map;
        }
        return new YamlScalarNode(value.ToString() ?? string.Empty);
    }

    private static YamlNode BuildScalarOrSeqOrMap(object value) =>
        value switch
        {
            string s => new YamlScalarNode(s),
            IEnumerable<string> seq => new YamlSequenceNode(
                seq.Select(x => (YamlNode)new YamlScalarNode(x))
            ),
            _ => BuildScalarOrMap(value),
        };

    private static string SerializeRoot(YamlMappingNode root)
    {
        // Use YamlDotNet's high-level serializer on the deserialized representation
        // model — round-trips the structure with default style. Comments are lost.
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var stream = new YamlStream(new YamlDocument(root));
        stream.Save(writer, assignAnchors: false);
        return sb.ToString();
    }

    private async Task<EditResult> ValidateAndWriteAsync(
        Workspace workspace,
        string conventionId,
        string fullPath,
        string newYaml,
        CancellationToken ct
    )
    {
        var (convention, error) = ConventionLoader.Parse(newYaml, sourcePath: null);
        if (convention is null || error is not null)
        {
            return EditResult.Failed(
                error?.Message ?? "Resulting YAML failed to parse as a convention."
            );
        }
        if (!string.Equals(convention.Id, conventionId, StringComparison.Ordinal))
        {
            return EditResult.Failed(
                $"Refusing to write — edited YAML's id ({convention.Id}) doesn't match expected id ({conventionId})."
            );
        }
        await File.WriteAllTextAsync(fullPath, newYaml, ct);
        return new EditResult(Convention: convention, ResultingYaml: newYaml, Error: null);
    }

    /// <summary>
    /// Suppress the unused-using warning for <see cref="YamlSerializer"/>.
    /// Reserved for upcoming structured-edit ops that go through the
    /// high-level deserializer (e.g. set_metadata_required).
    /// </summary>
    private static readonly ISerializer _yaml = YamlSerializer;
}

/// <summary>
/// Structured shape for a relationship edit. All fields except <see cref="Kind"/>
/// are optional — present fields are written, absent fields preserve whatever
/// the YAML had (or are omitted on add). YAML-side mapping uses
/// <c>snake_case</c>.
/// </summary>
public sealed record RelationshipEdit(
    string Kind,
    string? Name = null,
    string? Icon = null,
    string? Description = null,
    int? Order = null,
    object? Source = null,
    object? Filter = null,
    string? Interpret = null,
    object? TargetKind = null,
    string? Inverse = null,
    string? InverseName = null,
    string? InverseIcon = null,
    IReadOnlyDictionary<string, string>? Metadata = null
);

/// <summary>
/// Outcome of a mutating edit. <see cref="Convention"/> is the post-edit
/// state; <see cref="ResultingYaml"/> is what got written; <see cref="Error"/>
/// is non-null when the edit failed validation (in which case
/// <see cref="Convention"/> is null and nothing was written).
/// </summary>
public sealed record EditResult(Convention? Convention, string? ResultingYaml, string? Error)
{
    public bool Succeeded => Error is null && Convention is not null;

    public static EditResult Failed(string error) => new(null, null, error);
}

public sealed record ValidationResult(
    Convention? Convention,
    IReadOnlyList<ConventionLoadError> Errors
)
{
    public bool IsValid => Errors.Count == 0 && Convention is not null;
}

public sealed record TestResult(
    bool Matched,
    EntityProjection? Entity,
    IReadOnlyList<EntityRefProjection> Refs,
    ProjectionReport? Report,
    string? Error
)
{
    public static TestResult Failed(string error) =>
        new(false, null, Array.Empty<EntityRefProjection>(), null, error);
}
