using System.Text.Json;
using Creuser.Projections.Accessors;
using Creuser.Projections.Authoring;
using Creuser.Projections.Conventions;
using Creuser.Projections.Scanner;

namespace Creuser.Cli.Commands;

/// <summary>
/// <c>creuser conventions ...</c> subtree. Calls the same
/// <see cref="ConventionEditor"/> ops the API + assistant skill route through —
/// no HTTP, no auth: this is a local-fs editor for the operator.
/// </summary>
public static class ConventionsCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var sub = args[0];
            var rest = args[1..];
            return sub switch
            {
                "list" => await List(rest),
                "validate" => await Validate(rest),
                "test" => await Test(rest),
                "add-rel" => await AddRel(rest),
                _ => Unknown(sub),
            };
        }
        catch (CliUserError err)
        {
            Console.Error.WriteLine($"creuser: {err.Message}");
            return 2;
        }
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"creuser conventions: unknown subcommand '{sub}'.");
        Console.Error.WriteLine();
        PrintHelp();
        return 1;
    }

    private static bool IsHelp(string s) => s is "--help" or "-h" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("creuser conventions — workspace convention authoring");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  creuser conventions list [--workspace <path>]");
        Console.WriteLine("  creuser conventions validate <yaml-file>");
        Console.WriteLine(
            "  creuser conventions test <id> --against <relpath> [--workspace <path>]"
        );
        Console.WriteLine(
            "  creuser conventions add-rel <id> --kind <k> --source <s> [opts...] [--workspace <path>]"
        );
        Console.WriteLine();
        Console.WriteLine("add-rel options (mirror the YAML keys 1:1):");
        Console.WriteLine("  --kind <k>             Edge label, snake_case (required)");
        Console.WriteLine("  --name <n>             CDFS folder name (defaults to humanized kind)");
        Console.WriteLine("  --icon <icon>          Icon key");
        Console.WriteLine("  --description <d>     Tooltip / docs string");
        Console.WriteLine("  --order <int>          CDFS sort order (default 100)");
        Console.WriteLine(
            "  --source <s>           e.g. frontmatter.related / glob:packages/db/** / path-template:{file_dir}/index.md"
        );
        Console.WriteLine("  --filter-glob <p>      Filter by glob pattern");
        Console.WriteLine("  --filter-regex <p>     Filter by regex");
        Console.WriteLine(
            "  --filter-type <t>      Filter by post-classification type: path|glob|url|slug"
        );
        Console.WriteLine("  --interpret <mode>     auto (default) | path | slug | glob | url");
        Console.WriteLine("  --target-kind <k>      'any' | <kind> | 'k1,k2,k3'");
        Console.WriteLine("  --inverse <kind>       Reverse edge label");
        Console.WriteLine("  --inverse-name <n>     Reverse-edge folder name");
        Console.WriteLine("  --inverse-icon <i>     Reverse-edge folder icon");
    }

    // ---------- list ----------

    private static async Task<int> List(string[] args)
    {
        var p = ParsedArgs.Parse(args);
        var (workspace, path) = WorkspaceResolver.ResolveOrThrow(p.Get("workspace"));
        var loader = new ConventionLoader();
        var result = await loader.LoadAsync(workspace, path);
        if (result.Errors.Count > 0)
        {
            foreach (var err in result.Errors)
                Console.Error.WriteLine($"  [error] {err.Source ?? "(unknown)"}: {err.Message}");
        }
        if (result.Conventions.Count == 0)
        {
            Console.WriteLine("(no conventions declared)");
            return 0;
        }
        foreach (var c in result.Conventions)
        {
            Console.WriteLine($"{c.Id, -30} priority={c.Priority, -3}  glob={c.Match.Glob}");
            foreach (var r in c.Relationships)
            {
                var target = r.TargetKind.Any ? "any" : string.Join(",", r.TargetKind.Allowed);
                Console.WriteLine(
                    $"  - {r.Kind, -20} name={r.Name}  source={r.Source.Kind}.{r.Source.Key ?? "-"}  target={target}"
                );
            }
        }
        return 0;
    }

    // ---------- validate ----------

    private static async Task<int> Validate(string[] args)
    {
        var p = ParsedArgs.Parse(args);
        if (p.Positional.Count == 0)
            throw new CliUserError("validate: missing yaml-file argument.");
        var file = p.Positional[0];
        if (!File.Exists(file))
            throw new CliUserError($"validate: file not found: {file}");
        var yaml = await File.ReadAllTextAsync(file);
        var (workspace, path) = WorkspaceResolver.ResolveOrThrow(p.Get("workspace"));
        var editor = new ConventionEditor(WorkspaceResolver.TreeFor(path));
        var v = editor.Validate(yaml, file);
        if (v.IsValid)
        {
            Console.WriteLine(
                $"valid: id={v.Convention!.Id}  glob={v.Convention.Match.Glob}  rels={v.Convention.Relationships.Count}"
            );
            return 0;
        }
        Console.Error.WriteLine("invalid:");
        foreach (var err in v.Errors)
            Console.Error.WriteLine($"  - {err.Message}");
        return 1;
    }

    // ---------- test ----------

    private static async Task<int> Test(string[] args)
    {
        var p = ParsedArgs.Parse(args);
        if (p.Positional.Count == 0)
            throw new CliUserError("test: missing convention id argument.");
        var id = p.Positional[0];
        var against = p.GetRequired("against", "test");
        var (workspace, path) = WorkspaceResolver.ResolveOrThrow(p.Get("workspace"));
        var loader = new ConventionLoader();
        var scanner = new ProjectionScanner(TimeProvider.System, ComputedAccessorRegistry.Default);
        var editor = new ConventionEditor(WorkspaceResolver.TreeFor(path));
        var result = await editor.TestAsync(workspace, id, against, loader, scanner);
        if (!result.Matched)
        {
            Console.Error.WriteLine(result.Error ?? "no match.");
            return 1;
        }
        var entity = result.Entity!;
        Console.WriteLine($"matched: {entity.Kind}/{entity.Slug}  path={entity.Path}");
        Console.WriteLine($"  metadata: {Truncate(entity.MetadataJson, 200)}");
        if (result.Refs.Count == 0)
        {
            Console.WriteLine("  refs: (none)");
        }
        else
        {
            Console.WriteLine($"  refs ({result.Refs.Count}):");
            foreach (var r in result.Refs)
            {
                var status = r.ToEntityId is null ? "unresolved" : "resolved";
                Console.WriteLine(
                    $"    - {r.Relationship, -20} → {r.TargetKind ?? "?"}/{r.TargetSlug ?? "?"}  [{status}]"
                );
            }
        }
        return 0;
    }

    // ---------- add-rel ----------

    private static async Task<int> AddRel(string[] args)
    {
        var p = ParsedArgs.Parse(args);
        if (p.Positional.Count == 0)
            throw new CliUserError("add-rel: missing convention id argument.");
        var conventionId = p.Positional[0];
        var kind = p.GetRequired("kind", "add-rel");
        var source = p.GetRequired("source", "add-rel");

        var (workspace, path) = WorkspaceResolver.ResolveOrThrow(p.Get("workspace"));
        var editor = new ConventionEditor(WorkspaceResolver.TreeFor(path));

        var edit = new RelationshipEdit(
            Kind: kind,
            Name: p.Get("name"),
            Icon: p.Get("icon"),
            Description: p.Get("description"),
            Order: p.GetInt("order"),
            Source: source,
            Filter: BuildFilter(p),
            Interpret: p.Get("interpret"),
            TargetKind: BuildTargetKind(p.Get("target-kind")),
            Inverse: p.Get("inverse"),
            InverseName: p.Get("inverse-name"),
            InverseIcon: p.Get("inverse-icon"),
            Metadata: null
        );

        var result = await editor.AddRelationshipAsync(workspace, conventionId, edit);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"add-rel failed: {result.Error}");
            return 1;
        }
        var c = result.Convention!;
        Console.WriteLine(
            $"added: {conventionId}/{kind}  ({c.Relationships.Count} relationship{(c.Relationships.Count == 1 ? "" : "s")} on convention)"
        );
        return 0;
    }

    // ---------- helpers ----------

    private static object? BuildFilter(ParsedArgs p)
    {
        var glob = p.Get("filter-glob");
        if (!string.IsNullOrEmpty(glob))
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "glob",
                ["pattern"] = glob,
            };
        var regex = p.Get("filter-regex");
        if (!string.IsNullOrEmpty(regex))
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "regex",
                ["pattern"] = regex,
            };
        var type = p.Get("filter-type");
        if (!string.IsNullOrEmpty(type))
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "type",
                ["pattern"] = type,
            };
        return null;
    }

    private static object? BuildTargetKind(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        if (raw.Contains(','))
            return raw.Split(
                    ',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                )
                .ToList();
        return raw.Trim();
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "(empty)";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
