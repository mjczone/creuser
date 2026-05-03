using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Creuser.Core.Execution;
using Microsoft.Extensions.Logging;

namespace Creuser.Scripting;

/// <summary>
/// Deterministic step runner for HTTP requests. Built on
/// <see cref="IHttpClientFactory"/> so socket lifecycle + DNS refresh are
/// handled correctly. Inputs:
/// <list type="bullet">
///   <item><c>url</c> — full URL (required).</item>
///   <item><c>method</c> — GET / POST / PUT / PATCH / DELETE / HEAD. Defaults to GET.</item>
///   <item><c>headers</c> — dict of header name → value (e.g. <c>{ Authorization: "Bearer ..." }</c>).</item>
///   <item><c>query</c> — dict of query-param name → value, appended to the URL.</item>
///   <item><c>body</c> — string or object. If absent: no body. If a string: sent verbatim. If an object: serialized per <c>body_type</c>.</item>
///   <item><c>body_type</c> — <c>json</c> (default for object bodies) / <c>form</c> (URL-encoded) / <c>text</c>. Determines Content-Type when not explicit in <c>headers</c>.</item>
///   <item><c>timeout_seconds</c> — per-request timeout, default 30s. The runner-level <see cref="StepBudgets.MaxDuration"/> is the outer cap.</item>
///   <item><c>follow_redirects</c> — default true.</item>
///   <item><c>parse</c> — <c>auto</c> (Content-Type-driven, default) / <c>json</c> / <c>text</c> / <c>none</c>. <c>parsed</c> output carries the parsed shape; <c>body</c> always carries the raw response (capped, see below).</item>
///   <item><c>expected_status</c> — list of acceptable status codes. Default: any 2xx. Statuses outside the list mark the step failed.</item>
/// </list>
///
/// <para>
/// Outputs: <c>{ status, headers, body, parsed, latency_ms, content_type, url }</c>.
/// The <c>body</c> field is capped at 256 KB to keep audit-table jsonb fast;
/// the full response body is always written as a <c>response.body</c>
/// artifact (no cap), so the inspector can show the complete payload.
/// <c>body_truncated: true</c> appears in outputs when the cap kicked in.
/// </para>
///
/// <para>
/// SSRF posture: v0.1 is single-tenant on-prem; the operator is trusted, so
/// the runner does not block requests to private address ranges
/// (RFC 1918 / link-local / loopback). Multi-tenant deployments need a
/// pre-flight DNS resolution + IP allow/deny list — reserved for post-v1.
/// </para>
///
/// <para>
/// Caching is intentionally <em>not</em> in v0.1 — the wire shape is
/// forward-compatible (operators can re-run jobs to verify the runner
/// without cache; an HTTP cache table parallels the LLM cache when it
/// lands).
/// </para>
/// </summary>
public sealed class HttpStepRunner : IStepRunner
{
    public string StepType => "http";

    /// <summary>Cap on the size of the inline <c>body</c> output. Larger responses still go to the <c>response.body</c> artifact in full.</summary>
    public const int InlineBodyCapBytes = 256 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpStepRunner> _logger;

    public HttpStepRunner(IHttpClientFactory httpFactory, ILogger<HttpStepRunner> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();

        var url = GetString(inputs, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            sw.Stop();
            return StepResult.Failure("http step requires a `url` input.", sw.ElapsedMilliseconds);
        }

        var method = (GetString(inputs, "method") ?? "GET").ToUpperInvariant();
        if (!IsKnownMethod(method))
        {
            sw.Stop();
            return StepResult.Failure(
                $"http step `method` '{method}' is not recognized. Use GET / POST / PUT / PATCH / DELETE / HEAD.",
                sw.ElapsedMilliseconds
            );
        }

        // Build the final URL with query params appended.
        string finalUrl;
        try
        {
            finalUrl = AppendQueryParams(url, GetDict(inputs, "query"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return StepResult.Failure(
                $"http step could not build URL: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }

        // Resolve timeout. Per-request <= run-level budget if set.
        var timeout = TimeSpan.FromSeconds(
            GetDouble(inputs, "timeout_seconds") ?? DefaultTimeout.TotalSeconds
        );
        if (ctx.Budgets.MaxDuration is { } budget && timeout > budget)
            timeout = budget;

        var followRedirects = GetBool(inputs, "follow_redirects") ?? true;
        var expectedStatus = GetIntList(inputs, "expected_status");

        using var client = _httpFactory.CreateClient(
            followRedirects ? "creuser-http" : "creuser-http-noredirect"
        );

        var request = new HttpRequestMessage(new HttpMethod(method), finalUrl);
        ApplyHeaders(request, GetDict(inputs, "headers"));

        // Body. Object bodies serialize per body_type (default json); string
        // bodies pass through with the operator-supplied (or default) content type.
        var bodyError = TrySetBody(
            request,
            inputs.GetValueOrDefault("body"),
            GetString(inputs, "body_type")
        );
        if (bodyError is not null)
        {
            sw.Stop();
            return StepResult.Failure(bodyError, sw.ElapsedMilliseconds);
        }

        HttpResponseMessage response;
        byte[] bodyBytes;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token
            );
            bodyBytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return StepResult.Failure(
                $"http step timed out after {timeout.TotalSeconds:0}s contacting {finalUrl}.",
                sw.ElapsedMilliseconds
            );
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return StepResult.Failure(
                $"http step request failed: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "http step unexpected error contacting {Url}", finalUrl);
            return StepResult.Failure(
                $"http step unexpected error: {ex.GetType().Name}: {ex.Message}",
                sw.ElapsedMilliseconds
            );
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var bodyText = DecodeBody(bodyBytes, response.Content.Headers.ContentType);
        var (truncatedBody, wasTruncated) = TruncateBody(bodyText, InlineBodyCapBytes);

        // Parse per request, gracefully degrading to text on failure so the
        // step still surfaces the body for inspection.
        var parseMode = (GetString(inputs, "parse") ?? "auto").ToLowerInvariant();
        var (parsed, parseError) = ParseResponse(parseMode, contentType, bodyText);

        // Status handling. expected_status overrides default 2xx acceptance.
        var status = (int)response.StatusCode;
        var statusOk =
            expectedStatus.Count > 0
                ? expectedStatus.Contains(status)
                : status >= 200 && status <= 299;

        sw.Stop();
        var durationMs = sw.ElapsedMilliseconds;

        var headers = SerializeHeaders(response.Headers, response.Content.Headers);
        var outputs = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["headers"] = headers,
            ["body"] = truncatedBody,
            ["body_truncated"] = wasTruncated,
            ["parsed"] = parsed,
            ["latency_ms"] = durationMs,
            ["content_type"] = contentType,
            ["url"] = response.RequestMessage?.RequestUri?.ToString() ?? finalUrl,
        };

        var artifacts = new List<StepArtifact>
        {
            new StepArtifact("response.body", "response.body", bodyBytes, contentType),
            new StepArtifact(
                "response.headers",
                "response.headers.json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headers)),
                "application/json"
            ),
        };

        if (!statusOk)
        {
            return new StepResult(
                Status: StepStatus.Failed,
                Outputs: outputs,
                FileChanges: Array.Empty<FileChange>(),
                Artifacts: artifacts,
                DurationMs: durationMs,
                ErrorMessage: $"http step got status {status} {response.ReasonPhrase} (expected {(expectedStatus.Count > 0 ? string.Join("/", expectedStatus) : "2xx")})"
            );
        }

        if (parseError is not null)
        {
            // Don't fail on parse errors — the request itself succeeded; the
            // operator gets the raw body + an explanation in the error
            // message. Status remains succeeded.
            outputs["parse_error"] = parseError;
        }

        ctx.Logger.LogDebug(
            "http {Method} {Url} → {Status} in {Ms}ms",
            method,
            finalUrl,
            status,
            durationMs
        );

        return new StepResult(
            Status: StepStatus.Succeeded,
            Outputs: outputs,
            FileChanges: Array.Empty<FileChange>(),
            Artifacts: artifacts,
            DurationMs: durationMs
        );
    }

    private static bool IsKnownMethod(string method) =>
        method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";

    private static string AppendQueryParams(string url, IReadOnlyDictionary<string, object?>? query)
    {
        if (query is null || query.Count == 0)
            return url;
        var builder = new UriBuilder(url);
        var existing = builder.Query;
        var separator = string.IsNullOrEmpty(existing) || existing == "?" ? "" : "&";
        var sb = new StringBuilder(
            string.IsNullOrEmpty(existing) || existing == "?"
                ? string.Empty
                : existing.TrimStart('?')
        );
        foreach (var kv in query)
        {
            if (sb.Length > 0)
                sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value?.ToString() ?? string.Empty));
        }
        builder.Query = sb.ToString();
        _ = separator; // silence unused
        return builder.Uri.ToString();
    }

    private static void ApplyHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, object?>? headers
    )
    {
        if (headers is null)
            return;
        foreach (var kv in headers)
        {
            var value = kv.Value?.ToString();
            if (value is null)
                continue;
            // System.Net.HttpClient distinguishes "request" headers from
            // "content" headers; some keys (Content-Type, Content-Length)
            // can only live on the latter. Try the request collection first;
            // fall back to content if available.
            if (!request.Headers.TryAddWithoutValidation(kv.Key, value))
            {
                if (request.Content is null)
                {
                    // Defer — content headers without content are dropped;
                    // operators usually set Content-Type via body_type.
                    continue;
                }
                request.Content.Headers.TryAddWithoutValidation(kv.Key, value);
            }
        }
    }

    private static string? TrySetBody(HttpRequestMessage request, object? body, string? bodyType)
    {
        if (body is null)
            return null;

        var explicitType = bodyType?.ToLowerInvariant();

        if (body is string s)
        {
            // String body. Use explicit content type if given; otherwise
            // default to text/plain. Operators who want JSON can either set
            // body_type=json (and we'll trust their string is JSON) or set
            // Content-Type via headers.
            var contentType = explicitType switch
            {
                "json" => "application/json",
                "form" => "application/x-www-form-urlencoded",
                _ => "text/plain",
            };
            request.Content = new StringContent(s, Encoding.UTF8, contentType);
            return null;
        }

        // Object body. JSON-serialize unless body_type=form (then
        // form-encode the top-level dict).
        if (explicitType == "form")
        {
            if (body is not IReadOnlyDictionary<string, object?> dict)
                return "http step: body_type=form requires the body to be an object (key/value map).";
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in dict)
                pairs.Add(new(kv.Key, kv.Value?.ToString() ?? string.Empty));
            request.Content = new FormUrlEncodedContent(pairs);
            return null;
        }

        // Default: JSON-encode any object.
        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return null;
    }

    private static string DecodeBody(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        // Prefer charset from Content-Type, fall back to UTF-8. Binary
        // responses (image/*, application/octet-stream) round-trip lossy
        // through this — operators reading the artifact get the full bytes.
        var charset = contentType?.CharSet?.Trim('"');
        try
        {
            var encoding = string.IsNullOrEmpty(charset)
                ? Encoding.UTF8
                : Encoding.GetEncoding(charset);
            return encoding.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static (string Body, bool Truncated) TruncateBody(string body, int capBytes)
    {
        var byteCount = Encoding.UTF8.GetByteCount(body);
        if (byteCount <= capBytes)
            return (body, false);
        // Cut at a code-unit boundary; a UTF-8 character may cross. Decode
        // the cap-byte prefix then re-encode + re-decode to drop a partial
        // character at the tail.
        var capped = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(body), 0, capBytes);
        return (capped, true);
    }

    private static (object? Parsed, string? Error) ParseResponse(
        string parseMode,
        string contentType,
        string body
    )
    {
        switch (parseMode)
        {
            case "none":
                return (null, null);
            case "text":
                return (body, null);
            case "json":
                return TryParseJson(body);
            case "auto":
            default:
                if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    return TryParseJson(body);
                return (body, null);
        }
    }

    private static (object? Parsed, string? Error) TryParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Materialize to nested CLR shapes so it round-trips through the
            // outputs JSON column without holding a JsonDocument open.
            return (InputsNormalizer.Normalize(doc.RootElement.Clone()), null);
        }
        catch (JsonException ex)
        {
            return (null, $"Failed to parse response as JSON: {ex.Message}");
        }
    }

    private static Dictionary<string, string> SerializeHeaders(
        HttpResponseHeaders responseHeaders,
        HttpContentHeaders contentHeaders
    )
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in responseHeaders)
            dict[h.Key] = string.Join(", ", h.Value);
        foreach (var h in contentHeaders)
            dict[h.Key] = string.Join(", ", h.Value);
        return dict;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static double? GetDouble(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return null;
        return v switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            string s
                when double.TryParse(
                    s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d
                ) => d,
            _ => null,
        };
    }

    private static bool? GetBool(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return null;
        return v switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, object?>? GetDict(
        IReadOnlyDictionary<string, object?> inputs,
        string key
    )
    {
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return null;
        return v as IReadOnlyDictionary<string, object?>;
    }

    private static List<int> GetIntList(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        var result = new List<int>();
        if (!inputs.TryGetValue(key, out var v) || v is null)
            return result;
        if (v is IEnumerable<object?> seq)
        {
            foreach (var item in seq)
            {
                if (item is null)
                    continue;
                if (item is int i)
                    result.Add(i);
                else if (item is long l)
                    result.Add((int)l);
                else if (
                    int.TryParse(
                        item.ToString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed
                    )
                )
                    result.Add(parsed);
            }
        }
        return result;
    }
}
