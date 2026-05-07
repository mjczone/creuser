using Creuser.Cli.Commands;

namespace Creuser.Cli;

/// <summary>
/// Top-level command dispatch. Currently supports the <c>conventions</c>
/// subtree only — Creuser is single-tenant and the conventions surface is
/// the first one that matters from a terminal.
/// </summary>
public static class CliRouter
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintRootHelp();
            return 1;
        }

        var head = args[0];
        var rest = args[1..];
        return head switch
        {
            "conventions" => await ConventionsCommands.RunAsync(rest),
            "--help" or "-h" or "help" => Help(),
            "--version" or "-v" => Version(),
            _ => Unknown(head),
        };
    }

    private static int Help()
    {
        PrintRootHelp();
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine("creuser cli (development build)");
        return 0;
    }

    private static int Unknown(string head)
    {
        Console.Error.WriteLine($"creuser: unknown command '{head}'.");
        Console.Error.WriteLine();
        PrintRootHelp();
        return 1;
    }

    private static void PrintRootHelp()
    {
        Console.WriteLine("creuser — workspace conventions tooling");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  creuser conventions <subcommand> [args]");
        Console.WriteLine();
        Console.WriteLine("Convention subcommands:");
        Console.WriteLine("  list                              List declared conventions");
        Console.WriteLine(
            "  validate <yaml-file>              Parse a convention YAML and report errors"
        );
        Console.WriteLine(
            "  test <id> --against <path>        Dry-run scan one file against a convention"
        );
        Console.WriteLine(
            "  add-rel <id> --kind <k> ...       Add a relationship rule to a convention"
        );
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine(
            "  --workspace <path>                Workspace root (default: cwd, walks up to find .creuser/)"
        );
        Console.WriteLine("  --help, -h                        Show help");
        Console.WriteLine("  --version, -v                     Show version");
    }
}
