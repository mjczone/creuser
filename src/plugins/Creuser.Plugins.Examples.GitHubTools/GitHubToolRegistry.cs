using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Core.Repositories;
using Creuser.Core.Secrets;
using Creuser.Scripting.ToolLoop;
using Microsoft.Extensions.AI;

namespace Creuser.Plugins.Examples.GitHubTools;

/// <summary>
/// Plugin-contributed <see cref="IToolLoopToolRegistry"/>. Registers
/// three tools the agent can call inside an <c>llm-tool-loop</c> step:
/// <c>read_pr</c>, <c>list_issues</c>, <c>comment_on_issue</c>.
///
/// <para>
/// Credential resolution happens once per <see cref="BuildTools"/> call,
/// BEFORE the LLM sees the tool list — the registry reads workspace
/// plugin settings, fetches the PAT via <see cref="ISecretsReader"/>,
/// and bakes it into the closures. The LLM's tool-call args carry only
/// task-specific values (repo, pr number, issue body); credentials are
/// ambient.
/// </para>
/// </summary>
public sealed class GitHubToolRegistry : IToolLoopToolRegistry
{
    public static IReadOnlyList<string> ToolNames { get; } =
        new[] { "read_pr", "list_issues", "comment_on_issue" };

    public IReadOnlyList<string> AvailableTools => ToolNames;

    private static readonly JsonSerializerOptions SettingsJsonOptions = new(
        JsonSerializerDefaults.Web
    );

    private readonly IHttpClientFactory _http;
    private readonly ISecretsReader _secrets;
    private readonly IPluginSettingsStore _settings;

    public GitHubToolRegistry(
        IHttpClientFactory http,
        ISecretsReader secrets,
        IPluginSettingsStore settings
    )
    {
        _http = http;
        _secrets = secrets;
        _settings = settings;
    }

    public IReadOnlyList<AIFunction> BuildTools(
        IReadOnlyList<string> names,
        StepContext ctx,
        ToolLogSink sink
    )
    {
        // Resolve credentials + defaults once, ambient for all tools.
        var settingsJson = _settings
            .GetAsync(ctx.WorkspaceId, GitHubToolsPlugin.PluginId)
            .GetAwaiter()
            .GetResult();
        var settings = string.IsNullOrWhiteSpace(settingsJson)
            ? new GitHubSettings()
            : JsonSerializer.Deserialize<GitHubSettings>(settingsJson, SettingsJsonOptions)
                ?? new GitHubSettings();

        if (string.IsNullOrWhiteSpace(settings.PatSecretName))
            throw new ToolLoopException(
                "GitHub Tools plugin: workspace plugin settings missing `patSecretName`. "
                    + $"PUT /api/workspaces/{ctx.WorkspaceSlug}/plugins/{GitHubToolsPlugin.PluginId}/settings "
                    + "with `{ \"patSecretName\": \"github-pat\", ... }` and ensure /data/secrets/<name> exists."
            );
        var pat = _secrets.ReadAsync(settings.PatSecretName).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(pat))
            throw new ToolLoopException(
                $"GitHub Tools plugin: secret '{settings.PatSecretName}' is empty or missing."
            );

        var defaultRepo = settings.DefaultRepo;
        var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? "https://api.github.com/"
            : settings.BaseUrl!;

        var built = new List<AIFunction>(names.Count);
        foreach (var name in names)
        {
            AIFunction tool = name switch
            {
                "read_pr" => BuildReadPr(pat, baseUrl, defaultRepo, sink),
                "list_issues" => BuildListIssues(pat, baseUrl, defaultRepo, sink),
                "comment_on_issue" => BuildCommentOnIssue(pat, baseUrl, defaultRepo, sink),
                _ => throw new ToolLoopException(
                    $"GitHub Tools plugin: unknown tool '{name}'. Available: {string.Join(", ", AvailableTools)}."
                ),
            };
            built.Add(tool);
        }
        return built;
    }

    // ============================================================
    // Tool implementations
    // ============================================================

    private AIFunction BuildReadPr(
        string pat,
        string baseUrl,
        string? defaultRepo,
        ToolLogSink sink
    ) =>
        AIFunctionFactory.Create(
            async (
                // Optional params need explicit defaults — AIFunctionFactory
                // marks `string?` as required without one.
                [Description("Repo as owner/name (overrides workspace default).")] string? repo =
                    null,
                [Description("Pull request number.")] int number = 0,
                CancellationToken ct = default
            ) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(new { repo, number });
                var resolvedRepo = repo ?? defaultRepo;
                if (string.IsNullOrWhiteSpace(resolvedRepo))
                    return RecordResult(
                        sink,
                        "read_pr",
                        argsJson,
                        new { ok = false, error = "No repo specified and no workspace default." },
                        sw
                    );
                var result = await GetJsonAsync(
                    pat,
                    $"{baseUrl.TrimEnd('/')}/repos/{resolvedRepo}/pulls/{number}",
                    ct
                );
                return RecordResult(sink, "read_pr", argsJson, result, sw);
            },
            name: "read_pr",
            description: "Read a GitHub pull request by repo + number. Returns title, body, state, head/base refs, author, mergeable status."
        );

    private AIFunction BuildListIssues(
        string pat,
        string baseUrl,
        string? defaultRepo,
        ToolLogSink sink
    ) =>
        AIFunctionFactory.Create(
            async (
                [Description("Repo as owner/name (overrides workspace default).")]
                    string? repo = null,
                [Description("State filter: open / closed / all. Default open.")]
                    string? state = null,
                [Description("Cap on results. Default 30.")] int? limit = null,
                CancellationToken ct = default
            ) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(
                    new
                    {
                        repo,
                        state,
                        limit,
                    }
                );
                var resolvedRepo = repo ?? defaultRepo;
                if (string.IsNullOrWhiteSpace(resolvedRepo))
                    return RecordResult(
                        sink,
                        "list_issues",
                        argsJson,
                        new { ok = false, error = "No repo specified and no workspace default." },
                        sw
                    );
                var queryState = string.IsNullOrWhiteSpace(state) ? "open" : state;
                var perPage = Math.Max(1, Math.Min(100, limit ?? 30));
                var url =
                    $"{baseUrl.TrimEnd('/')}/repos/{resolvedRepo}/issues?state={queryState}&per_page={perPage}";
                var result = await GetJsonAsync(pat, url, ct);
                return RecordResult(sink, "list_issues", argsJson, result, sw);
            },
            name: "list_issues",
            description: "List issues for a repo, optionally filtered by state (open / closed / all). Note: GitHub's REST API includes pull requests in the issues list — filter by `pull_request` field if you need issues only."
        );

    private AIFunction BuildCommentOnIssue(
        string pat,
        string baseUrl,
        string? defaultRepo,
        ToolLogSink sink
    ) =>
        AIFunctionFactory.Create(
            async (
                [Description("Issue or pull request number.")] int number,
                [Description("Comment body (markdown).")] string body,
                [Description("Repo as owner/name (overrides workspace default).")]
                    string? repo = null,
                CancellationToken ct = default
            ) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var argsJson = JsonSerializer.Serialize(
                    new
                    {
                        repo,
                        number,
                        body_length = body.Length,
                    }
                );
                var resolvedRepo = repo ?? defaultRepo;
                if (string.IsNullOrWhiteSpace(resolvedRepo))
                    return RecordResult(
                        sink,
                        "comment_on_issue",
                        argsJson,
                        new { ok = false, error = "No repo specified and no workspace default." },
                        sw
                    );
                var url = $"{baseUrl.TrimEnd('/')}/repos/{resolvedRepo}/issues/{number}/comments";
                try
                {
                    using var client = _http.CreateClient("github-plugin");
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = JsonContent.Create(new { body }),
                    };
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
                    using var resp = await client.SendAsync(req, ct);
                    var respBody = await resp.Content.ReadAsStringAsync(ct);
                    var parsed = TryDeserialize(respBody);
                    var result = new
                    {
                        ok = resp.IsSuccessStatusCode,
                        http_status = (int)resp.StatusCode,
                        comment = parsed,
                    };
                    return RecordResult(sink, "comment_on_issue", argsJson, result, sw);
                }
                catch (Exception ex)
                {
                    return RecordResult(
                        sink,
                        "comment_on_issue",
                        argsJson,
                        new { ok = false, error = ex.Message },
                        sw
                    );
                }
            },
            name: "comment_on_issue",
            description: "Post a comment on a GitHub issue or pull request. Returns the created comment's id, body, and timestamp on success."
        );

    private async Task<object> GetJsonAsync(string pat, string url, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient("github-plugin");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = TryDeserialize(body);
            return new
            {
                ok = resp.IsSuccessStatusCode,
                http_status = (int)resp.StatusCode,
                data = parsed,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static object? TryDeserialize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return body;
        }
    }

    private static object RecordResult(
        ToolLogSink sink,
        string tool,
        string argsJson,
        object result,
        System.Diagnostics.Stopwatch sw
    )
    {
        sw.Stop();
        sink.Record(
            new ToolLogEntry(
                Turn: sink.CurrentTurn,
                Tool: tool,
                ArgsJson: argsJson,
                ResultJson: JsonSerializer.Serialize(result),
                DurationMs: sw.ElapsedMilliseconds
            )
        );
        return result;
    }
}
