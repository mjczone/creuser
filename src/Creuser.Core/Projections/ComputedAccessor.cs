namespace Creuser.Core.Projections;

/// <summary>
/// Registry contract for the computed-field accessors a convention can name in
/// <see cref="ConventionMetadataSpec.Computed"/>. Each namespace (<c>file</c>,
/// <c>git</c>, <c>path</c>, <c>body</c>, …) groups a fixed, documented set of
/// <see cref="AccessorField"/> entries.
///
/// <para>
/// The registry is the single source of truth for both the runtime resolver
/// (<c>ProjectionScanner</c>) and the published JSON Schema. Authors consult
/// the schema (or the generated reference docs) to learn which accessors are
/// available — there is no hidden surface area in the resolver's switch
/// statement anymore.
/// </para>
/// </summary>
public interface IComputedAccessorNamespace
{
    /// <summary>Namespace name; appears as the prefix in <c>&lt;ns&gt;.&lt;field&gt;</c>.</summary>
    string Namespace { get; }

    /// <summary>Optional human-readable description for ref docs / schema tooltips.</summary>
    string? Description { get; }

    /// <summary>Fields exposed by this namespace, keyed by field name (without the namespace prefix).</summary>
    IReadOnlyDictionary<string, AccessorField> Fields { get; }
}

/// <summary>
/// One accessor field declaration. <see cref="ReturnType"/> is informational
/// (drives schema annotations); <see cref="Resolve"/> performs the actual
/// computation against an <see cref="AccessorContext"/>.
/// </summary>
public sealed record AccessorField(
    string Name,
    string Description,
    AccessorReturnType ReturnType,
    Func<AccessorContext, object?> Resolve
);

/// <summary>
/// Coarse return-type marker used by the schema generator. Values are kept
/// JSON-Schema-aligned so the generator can map directly.
/// </summary>
public enum AccessorReturnType
{
    String,
    Integer,
    Number,
    Boolean,
    DateTime,
    StringArray,
    Object,
}

/// <summary>
/// Per-entity context an accessor uses to compute its value. Filled in by the
/// scanner before invoking accessors for one file. Fields are nullable so
/// new accessor namespaces can opt out of inputs they don't need.
/// </summary>
public sealed record AccessorContext(
    string FullPath,
    string RelativePath,
    IReadOnlyDictionary<string, object?>? Frontmatter,
    Func<byte[]>? ReadBytes
);
