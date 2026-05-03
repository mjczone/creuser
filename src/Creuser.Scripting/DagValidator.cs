namespace Creuser.Scripting;

/// <summary>
/// Validates a multi-step job's DAG and produces the topological execution
/// order. Catches the four classes of structural error a hand-authored
/// script can introduce:
/// <list type="bullet">
///   <item>Empty / blank step ids.</item>
///   <item>Duplicate ids — bindings would be ambiguous.</item>
///   <item><c>depends_on</c> referencing an unknown step id.</item>
///   <item>Cycles in the dependency graph — would deadlock.</item>
/// </list>
///
/// <para>
/// On success, <see cref="ValidationResult.Sorted"/> is the steps in
/// topological order — the executor walks this list left to right. When
/// multiple steps are eligible at the same wave (no remaining
/// dependencies), the validator preserves the authored order so the audit
/// timeline matches operator intent. (Parallel-eligible steps still
/// execute sequentially in v0.1; per-wave parallelism is a follow-up.)
/// </para>
/// </summary>
internal static class DagValidator
{
    public sealed record ValidationResult(string? Error, IReadOnlyList<JobScriptStepDecl> Sorted);

    public static ValidationResult Validate(IReadOnlyList<JobScriptStepDecl> steps)
    {
        if (steps is null || steps.Count == 0)
            return new ValidationResult("DAG has no steps.", Array.Empty<JobScriptStepDecl>());

        // Validate ids: present, unique.
        var byId = new Dictionary<string, JobScriptStepDecl>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (string.IsNullOrWhiteSpace(s.Id))
                return new ValidationResult(
                    $"steps[{i}] is missing required `id`.",
                    Array.Empty<JobScriptStepDecl>()
                );
            if (byId.ContainsKey(s.Id))
                return new ValidationResult(
                    $"Duplicate step id '{s.Id}' (positions {GetPosition(steps, s.Id)} and {i}).",
                    Array.Empty<JobScriptStepDecl>()
                );
            byId[s.Id] = s;
            if (string.IsNullOrWhiteSpace(s.Type))
                return new ValidationResult(
                    $"Step '{s.Id}' is missing required `type`.",
                    Array.Empty<JobScriptStepDecl>()
                );
        }

        // Validate depends_on edges target known ids.
        foreach (var s in steps)
        {
            foreach (var dep in s.DependsOn)
            {
                if (string.IsNullOrWhiteSpace(dep))
                    return new ValidationResult(
                        $"Step '{s.Id}' has an empty entry in `depends_on`.",
                        Array.Empty<JobScriptStepDecl>()
                    );
                if (!byId.ContainsKey(dep))
                    return new ValidationResult(
                        $"Step '{s.Id}' depends on unknown step '{dep}'. Known step ids: {string.Join(", ", byId.Keys)}.",
                        Array.Empty<JobScriptStepDecl>()
                    );
                if (string.Equals(dep, s.Id, StringComparison.Ordinal))
                    return new ValidationResult(
                        $"Step '{s.Id}' depends on itself.",
                        Array.Empty<JobScriptStepDecl>()
                    );
            }
        }

        // Topological sort: Kahn's algorithm preserving authored order on
        // ties. Track in-degree; emit any zero-in-degree node, decrement
        // dependents' in-degrees; repeat. Cycle iff some nodes remain
        // un-emitted at the end.
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            inDegree[s.Id] = s.DependsOn.Count;
            dependents[s.Id] = new List<string>();
        }
        foreach (var s in steps)
        {
            foreach (var dep in s.DependsOn)
                dependents[dep].Add(s.Id);
        }

        // Authored-order queue: walk steps left-to-right repeatedly,
        // emitting any whose in-degree is zero. This is O(N²) but N is
        // small in practice (job DAGs of dozens, not thousands).
        var sorted = new List<JobScriptStepDecl>(steps.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in steps)
            {
                if (emitted.Contains(s.Id))
                    continue;
                if (inDegree[s.Id] != 0)
                    continue;
                sorted.Add(s);
                emitted.Add(s.Id);
                foreach (var dependent in dependents[s.Id])
                    inDegree[dependent]--;
                changed = true;
            }
        }

        if (sorted.Count != steps.Count)
        {
            var unsorted = steps.Where(s => !emitted.Contains(s.Id)).Select(s => s.Id).ToList();
            return new ValidationResult(
                $"DAG contains a cycle involving: {string.Join(", ", unsorted)}.",
                Array.Empty<JobScriptStepDecl>()
            );
        }

        return new ValidationResult(null, sorted);
    }

    private static int GetPosition(IReadOnlyList<JobScriptStepDecl> steps, string id)
    {
        for (var i = 0; i < steps.Count; i++)
            if (string.Equals(steps[i].Id, id, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
