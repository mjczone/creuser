using Creuser.Core.Projections;

namespace Creuser.Projections.Accessors;

/// <summary>
/// Aggregates the <see cref="IComputedAccessorNamespace"/> implementations into
/// a flat lookup keyed by the dotted accessor name (<c>file.line_count</c>).
/// Workspaces can reuse <see cref="Default"/> directly or compose a custom
/// registry when they want extra namespaces.
/// </summary>
public sealed class ComputedAccessorRegistry
{
    private readonly Dictionary<string, AccessorField> _byDotted;
    private readonly IReadOnlyList<IComputedAccessorNamespace> _namespaces;

    public ComputedAccessorRegistry(IEnumerable<IComputedAccessorNamespace> namespaces)
    {
        _namespaces = namespaces.ToList();
        _byDotted = new Dictionary<string, AccessorField>(StringComparer.Ordinal);
        foreach (var ns in _namespaces)
        {
            foreach (var (key, field) in ns.Fields)
            {
                _byDotted[$"{ns.Namespace}.{key}"] = field;
            }
        }
    }

    public IReadOnlyList<IComputedAccessorNamespace> Namespaces => _namespaces;

    public bool TryGet(string accessor, out AccessorField field) =>
        _byDotted.TryGetValue(accessor, out field!);

    public IReadOnlyDictionary<string, AccessorField> AllFields => _byDotted;

    /// <summary>
    /// Default registry: <c>file</c>, <c>path</c>, <c>body</c>. <c>git</c> is
    /// reserved (its accessors land alongside the working-tree git shell-out
    /// in a follow-up stage and are intentionally absent here so the schema
    /// doesn't promise what the resolver can't deliver).
    /// </summary>
    public static ComputedAccessorRegistry Default { get; } =
        new(
            new IComputedAccessorNamespace[]
            {
                new FileAccessorNamespace(),
                new PathAccessorNamespace(),
                new BodyAccessorNamespace(),
            }
        );
}
