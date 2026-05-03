using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Creuser.Core.Execution;
using Creuser.Scripting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Creuser.Scripting.Tests;

public class HttpStepRunnerTests
{
    [Fact]
    public void StepType_IsHttp()
    {
        var (runner, _) = BuildRunner((req, _) => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal("http", runner.StepType);
    }

    [Fact]
    public async Task Execute_NoUrl_FailsWithMessage()
    {
        var (runner, _) = BuildRunner((req, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>(),
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`url`", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_UnknownMethod_Fails()
    {
        var (runner, _) = BuildRunner((req, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://example.test/foo",
                ["method"] = "TELEPORT",
            },
            CancellationToken.None
        );
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("`method`", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_BasicGet_RecordsStatusAndBody()
    {
        var (runner, captured) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"hello\":\"world\"}",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/v1/widgets",
                ["method"] = "GET",
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(200, (int)result.Outputs["status"]!);
        Assert.Equal("{\"hello\":\"world\"}", (string)result.Outputs["body"]!);
        // Auto-parse picks up JSON when Content-Type says so.
        var parsed = (Dictionary<string, object?>)result.Outputs["parsed"]!;
        Assert.Equal("world", parsed["hello"]);

        // Captured request side: method + URL match.
        Assert.Equal(HttpMethod.Get, captured.Last.Method);
        Assert.Equal("https://api.example.test/v1/widgets", captured.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Execute_QueryParams_AppendedToUrl()
    {
        var (runner, captured) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
        );
        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/search",
                ["query"] = new Dictionary<string, object?> { ["q"] = "creuser", ["limit"] = 50 },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        var url = captured.Last.RequestUri!.ToString();
        Assert.Contains("q=creuser", url);
        Assert.Contains("limit=50", url);
    }

    [Fact]
    public async Task Execute_HeadersForwarded()
    {
        var (runner, captured) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
        );

        await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/",
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Authorization"] = "Bearer secret-token",
                    ["X-Custom"] = "yes",
                },
            },
            CancellationToken.None
        );

        Assert.Equal("Bearer secret-token", captured.Last.Headers.Authorization?.ToString());
        Assert.True(captured.Last.Headers.Contains("X-Custom"));
    }

    [Fact]
    public async Task Execute_PostObjectBody_JsonEncoded()
    {
        var (runner, captured) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("ok"),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/widgets",
                ["method"] = "POST",
                ["body"] = new Dictionary<string, object?> { ["name"] = "shiny", ["count"] = 42 },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(201, (int)result.Outputs["status"]!);

        // Inspect the recorded request body.
        var sentBody = await captured.Last.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"shiny\"", sentBody);
        Assert.Contains("\"count\":42", sentBody);
        Assert.Equal("application/json", captured.Last.Content!.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Execute_PostFormBody_FormEncoded()
    {
        var (runner, captured) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
        );

        await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/login",
                ["method"] = "POST",
                ["body_type"] = "form",
                ["body"] = new Dictionary<string, object?>
                {
                    ["username"] = "alice",
                    ["password"] = "secret",
                },
            },
            CancellationToken.None
        );

        var sentBody = await captured.Last.Content!.ReadAsStringAsync();
        Assert.Equal(
            "application/x-www-form-urlencoded",
            captured.Last.Content!.Headers.ContentType?.MediaType
        );
        Assert.Contains("username=alice", sentBody);
        Assert.Contains("password=secret", sentBody);
    }

    [Fact]
    public async Task Execute_PostStringBody_PassedThrough()
    {
        var (runner, captured) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
        );

        await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/raw",
                ["method"] = "POST",
                ["body"] = "<xml>raw</xml>",
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Content-Type"] = "application/xml",
                },
            },
            CancellationToken.None
        );

        var sentBody = await captured.Last.Content!.ReadAsStringAsync();
        Assert.Equal("<xml>raw</xml>", sentBody);
    }

    [Fact]
    public async Task Execute_NonSuccessStatus_FailsByDefault()
    {
        var (runner, _) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("nope"),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["url"] = "https://api.example.test/missing" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal(404, (int)result.Outputs["status"]!);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_ExpectedStatusList_AcceptsListedStatus()
    {
        var (runner, _) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("ok-by-design"),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/probe",
                // Operator probing for a 404 specifically — succeed when it
                // happens, fail on anything else.
                ["expected_status"] = new List<object?> { 404 },
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(404, (int)result.Outputs["status"]!);
    }

    [Fact]
    public async Task Execute_ParseTextOnJson_KeepsBodyAsString()
    {
        var (runner, _) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"x\":1}", Encoding.UTF8, "application/json"),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/",
                ["parse"] = "text",
            },
            CancellationToken.None
        );

        Assert.Equal("{\"x\":1}", (string)result.Outputs["parsed"]!);
    }

    [Fact]
    public async Task Execute_ParseNoneOnAnything_LeavesParsedNull()
    {
        var (runner, _) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("anything"),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/",
                ["parse"] = "none",
            },
            CancellationToken.None
        );

        Assert.Null(result.Outputs["parsed"]);
    }

    [Fact]
    public async Task Execute_MalformedJson_StepStillSucceedsWithParseError()
    {
        var (runner, _) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{this is not json",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["url"] = "https://api.example.test/bad-json" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Null(result.Outputs["parsed"]);
        Assert.Contains("parse_error", result.Outputs.Keys);
    }

    [Fact]
    public async Task Execute_LargeResponse_TruncatesInlineBodyButKeepsArtifact()
    {
        // 300KB body — over the 256KB inline cap.
        var bigBody = new string('x', 300 * 1024);
        var (runner, _) = BuildRunner(
            (req, _) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bigBody) }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["url"] = "https://api.example.test/big" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.True((bool)result.Outputs["body_truncated"]!);
        // Inline body capped at 256KB (codepoint, but x is single-byte).
        Assert.Equal(HttpStepRunner.InlineBodyCapBytes, ((string)result.Outputs["body"]!).Length);
        // Full body in the artifact.
        var artifact = result.Artifacts.First(a => a.Kind == "response.body");
        Assert.Equal(bigBody.Length, artifact.Content.Length);
    }

    [Fact]
    public async Task Execute_NetworkException_MappedToStepFailure()
    {
        var (runner, _) = BuildRunner(
            (req, _) => throw new HttpRequestException("DNS resolution failed")
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?> { ["url"] = "https://nonexistent.example.test/" },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("DNS resolution failed", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_TimeoutFiresClean()
    {
        var (runner, _) = BuildAsyncRunner(
            async (req, ct) =>
            {
                // Hold the request open until cancellation; the runner's
                // per-request timeout (1s) should cancel us.
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );

        var result = await runner.ExecuteAsync(
            BuildContext(),
            new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.test/slow",
                ["timeout_seconds"] = 1,
            },
            CancellationToken.None
        );

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    private static StepContext BuildContext() =>
        new(
            RunId: Guid.NewGuid(),
            WorkspaceId: Guid.NewGuid(),
            WorkspaceSlug: "test-ws",
            WorkingTreePath: Path.GetTempPath(),
            StepId: Guid.NewGuid(),
            StepName: "http test",
            Budgets: new StepBudgets(),
            Logger: NullLogger.Instance,
            AllowedCommands: null,
            RequiredSecrets: null,
            ResumeToken: null
        );

    private static (HttpStepRunner Runner, CapturedRequests Captured) BuildRunner(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond
    )
    {
        var captured = new CapturedRequests();
        var handler = new StubHandler(
            async (req, ct) =>
            {
                captured.Record(req);
                return await Task.FromResult(respond(req, ct));
            }
        );
        var factory = new StubHttpClientFactory(handler);
        var runner = new HttpStepRunner(factory, NullLogger<HttpStepRunner>.Instance);
        return (runner, captured);
    }

    private static (HttpStepRunner Runner, CapturedRequests Captured) BuildAsyncRunner(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
    )
    {
        var captured = new CapturedRequests();
        var handler = new StubHandler(
            async (req, ct) =>
            {
                captured.Record(req);
                return await respond(req, ct);
            }
        );
        var factory = new StubHttpClientFactory(handler);
        var runner = new HttpStepRunner(factory, NullLogger<HttpStepRunner>.Instance);
        return (runner, captured);
    }

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();

        public void Record(HttpRequestMessage req) => _items.Add(req);

        public HttpRequestMessage Last => _items[^1];
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>
        > _respond;

        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
        )
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => _respond(request, cancellationToken);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
