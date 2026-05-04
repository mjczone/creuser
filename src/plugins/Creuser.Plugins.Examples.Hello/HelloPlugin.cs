using Creuser.Core.Execution;
using Creuser.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creuser.Plugins.Examples.Hello;

/// <summary>
/// Minimal Creuser plugin — the "smallest possible plugin" demo. Single
/// extension point: contributes one <see cref="IStepRunner"/>
/// (<c>hello-world</c>) that echoes its inputs back as a greeting.
///
/// <para>
/// Deploy: build via <c>dotnet publish</c>, copy the output folder to
/// <c>&lt;dataDir&gt;/plugins/creuser.examples.hello/</c>, restart Creuser.
/// The plugin appears in the Plugins page; admins enable per workspace;
/// a workspace can then declare <c>type: hello-world</c> in a job script.
/// </para>
///
/// <para>
/// Read this file end-to-end before writing your own plugin — every line
/// is intentional and the patterns scale up to non-trivial plugins.
/// </para>
/// </summary>
public sealed class HelloPlugin : IPluginRegistration
{
    public PluginManifest Manifest { get; } =
        new(
            Id: "creuser.examples.hello",
            Name: "Hello World Example",
            Version: "0.1.0",
            Author: "MJCZone",
            Description: "Smallest possible Creuser plugin — contributes a `hello-world` step runner that echoes its input. Read its source as the canonical 'how to write a plugin' reference.",
            MinimumHostVersion: "0.1.0",
            Provides: new[] { "StepRunner:hello-world" },
            DocumentationUrl: "https://github.com/mjczone/creuser/blob/main/docs/plugin-development.md"
        );

    public void Configure(IServiceCollection services, IPluginContext context)
    {
        // One contribution: a step runner registered under the keyed
        // service name "hello-world". The host's JobExecutor /
        // StepDispatchHandler resolves keyed IStepRunner by step type;
        // any plugin can contribute additional types using the same
        // pattern.
        services.AddKeyedScoped<IStepRunner, HelloWorldStepRunner>("hello-world");
        context.Logger.LogInformation(
            "Hello plugin registered hello-world step runner from {Dir}",
            context.PluginDirectory
        );
    }
}
