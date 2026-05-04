using System.Diagnostics;
using Creuser.Core.Execution;

namespace Creuser.Plugins.Examples.Hello;

/// <summary>
/// <c>type: hello-world</c> step runner. Inputs:
/// <list type="bullet">
///   <item><c>name</c> (string, optional, default <c>"world"</c>)</item>
/// </list>
/// Outputs:
/// <list type="bullet">
///   <item><c>greeting</c> (string)</item>
///   <item><c>name</c> (string — echoed back)</item>
/// </list>
///
/// <para>
/// Trivial implementation by design — the plugin's value is showing the
/// shape of an <see cref="IStepRunner"/> contribution, not the
/// computation. Plugin authors copy this file, change the
/// <see cref="StepType"/> + <see cref="ExecuteAsync"/> body, and ship.
/// </para>
/// </summary>
public sealed class HelloWorldStepRunner : IStepRunner
{
    public string StepType => "hello-world";

    public Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        var name = inputs.TryGetValue("name", out var rawName) ? rawName?.ToString() : null;
        if (string.IsNullOrWhiteSpace(name))
            name = "world";

        var greeting = $"Hello, {name}!";
        sw.Stop();

        var outputs = new Dictionary<string, object?> { ["greeting"] = greeting, ["name"] = name };
        return Task.FromResult(StepResult.Success(outputs, sw.ElapsedMilliseconds));
    }
}
