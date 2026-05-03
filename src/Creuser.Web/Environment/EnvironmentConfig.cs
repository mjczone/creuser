namespace Creuser.Web.Environment;

/// <summary>
/// Singleton platform-environment configuration. Stored as a row in
/// <c>cr.app_settings</c> under key <c>environment</c>. This is the
/// admin-facing config for things that affect the deployment as a whole:
/// SMTP, AI providers + default models, base URL.
///
/// <para>
/// Secrets (API keys, SMTP password) are never stored in this record —
/// only references to filenames under <c>&lt;dataDir&gt;/secrets/</c>.
/// The actual values live on disk (chmod 600) and are read by domain code
/// when it needs to make an outbound call.
/// </para>
/// </summary>
public sealed record EnvironmentConfig(
    GeneralConfig General,
    SmtpConfig Smtp,
    AiProvidersConfig AiProviders
)
{
    public static EnvironmentConfig Default { get; } =
        new(
            General: new GeneralConfig(),
            Smtp: new SmtpConfig(),
            AiProviders: new AiProvidersConfig()
        );
}

public sealed record GeneralConfig(
    /// <summary>External URL the app is served at — used in emails, webhooks. e.g. <c>https://creuser.example.com</c>.</summary>
    string? BaseUrl = null,
    /// <summary>IANA timezone string for the deployment (e.g. <c>America/New_York</c>). Defaults to UTC.</summary>
    string? Timezone = null
);

public sealed record SmtpConfig(
    string? Host = null,
    int? Port = null,
    string? Username = null,
    /// <summary>Filename of the SMTP password under <c>/data/secrets/</c>.</summary>
    string? PasswordSecret = null,
    /// <summary>One of <c>none</c>, <c>starttls</c>, <c>tls</c>.</summary>
    string? Encryption = null,
    string? FromAddress = null,
    string? FromName = null
);

public sealed record AiProvidersConfig(
    /// <summary>Default provider for in-app chat / agent runs. One of <c>anthropic</c>, <c>openai</c>, <c>local</c>.</summary>
    string? DefaultProvider = null,
    AnthropicConfig? Anthropic = null,
    OpenAIConfig? OpenAI = null,
    LocalProviderConfig? Local = null
);

public sealed record AnthropicConfig(
    /// <summary>Filename of the Anthropic API key under <c>/data/secrets/</c>.</summary>
    string? ApiKeySecret = null,
    /// <summary>Default Claude model id, e.g. <c>claude-opus-4-7</c>.</summary>
    string? DefaultModel = null,
    /// <summary>Optional override base URL (for Bedrock / corporate proxies).</summary>
    string? BaseUrl = null
);

public sealed record OpenAIConfig(
    /// <summary>Filename of the OpenAI API key under <c>/data/secrets/</c>.</summary>
    string? ApiKeySecret = null,
    /// <summary>Default model id, e.g. <c>gpt-5</c>.</summary>
    string? DefaultModel = null,
    /// <summary>Optional override base URL (Azure OpenAI, corporate proxies).</summary>
    string? BaseUrl = null,
    /// <summary>Optional Azure deployment name when using an Azure OpenAI base URL.</summary>
    string? AzureDeployment = null
);

/// <summary>
/// OpenAI-compatible local provider — Ollama, LM Studio, vLLM, or any
/// other server that speaks the OpenAI Chat Completions wire format. Lives
/// alongside (not instead of) the cloud OpenAI config so an admin can
/// configure both: cloud for production, local for dev / cheap inference.
///
/// Internally this routes through the OpenAI client with a custom endpoint;
/// the only practical difference from <see cref="OpenAIConfig"/> is that
/// <see cref="BaseUrl"/> is required and the API key is optional (most
/// local servers don't authenticate).
/// </summary>
public sealed record LocalProviderConfig(
    /// <summary>Required endpoint URL — e.g. <c>http://localhost:11434/v1</c> for Ollama, <c>http://localhost:1234/v1</c> for LM Studio.</summary>
    string? BaseUrl = null,
    /// <summary>Free-text model identifier — e.g. <c>llama3.1</c>, <c>qwen2.5-coder:32b</c>, <c>gpt-oss-120b</c>.</summary>
    string? DefaultModel = null,
    /// <summary>Filename of the API key under <c>/data/secrets/</c>. Optional; most local servers don't authenticate.</summary>
    string? ApiKeySecret = null,
    /// <summary>Convenience tag — <c>ollama</c>, <c>lmstudio</c>, or <c>custom</c>. UI hint only; doesn't affect runtime behavior.</summary>
    string? Kind = null
);

/// <summary>
/// Wire shape returned by <c>GET /api/environment</c>. The structured
/// config is the same as <see cref="EnvironmentConfig"/>; the
/// <see cref="SecretsPresent"/> map tells the UI which secret filenames
/// currently have on-disk values so the SecretInput field can render a
/// "set" / "not set" indicator without ever exposing the secret itself.
/// </summary>
public sealed record EnvironmentConfigView(
    EnvironmentConfig Config,
    IReadOnlyDictionary<string, bool> SecretsPresent
);
