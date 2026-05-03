using Creuser.Auth.Abstractions;

namespace Creuser.Web.Agents.Capabilities;

/// <summary>
/// Hand-curated list of the platform's built-in capabilities — Settings
/// surfaces, admin actions, operator workflows. Lives in source so the
/// catalog moves with the endpoints (PRs that add a feature touch this
/// file too; the surface stays honest under refactor).
///
/// When the <c>[AiCapability]</c> attribute scanner lands, most of these
/// entries can be replaced by attributes on the corresponding endpoint
/// methods — same shape, derived automatically. Until then, this is the
/// authoritative list.
/// </summary>
public sealed class CoreCapabilityProvider : ICapabilityProvider
{
    private static readonly IReadOnlyList<Capability> All =
    [
        // ─── Settings → Branding ──────────────────────────────────────────
        new(
            Id: "branding.theme",
            Topic: "branding",
            Title: "Branding & theme",
            Description: "Configure the product name, logo, login tagline, color palette, chrome tokens, fonts, and custom CSS for the entire deployment.",
            Intents:
            [
                "change theme",
                "change colors",
                "set logo",
                "rebrand",
                "white label",
                "change product name",
                "change palette",
                "custom css",
                "change font",
                "change favicon",
            ],
            Route: "/settings/branding",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "branding.preset",
            Topic: "branding",
            Title: "Pick a theme preset",
            Description: "Apply a curated palette (GitHub, Solarized, Dracula, Tokyo Night, Catppuccin Latte, etc.) in one click.",
            Intents: ["pick theme", "preset", "github theme", "dracula", "solarized"],
            Route: "/settings/branding",
            ExpandSection: "presets",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        // ─── Settings → Users ─────────────────────────────────────────────
        new(
            Id: "users.invite",
            Topic: "users",
            Title: "Invite a user",
            Description: "Create a new user account with email, display name, and role. The server returns a one-time temporary password the admin shares out-of-band.",
            Intents: ["invite user", "add user", "create account", "new user"],
            Route: "/settings/users",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "users.manage",
            Topic: "users",
            Title: "Manage user accounts",
            Description: "Reset a user's password, toggle their role between Admin and User, deactivate them, or delete the account. The last remaining admin can't be demoted, deactivated, or deleted.",
            Intents:
            [
                "reset password",
                "change role",
                "promote user",
                "demote user",
                "deactivate user",
                "delete user",
                "remove user",
            ],
            Route: "/settings/users",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        // ─── Settings → Environment ───────────────────────────────────────
        new(
            Id: "environment.general",
            Topic: "environment",
            Title: "Base URL & timezone",
            Description: "Set the deployment's external URL (used in email links, webhooks) and IANA timezone for human-readable timestamps.",
            Intents: ["set base url", "change timezone", "deployment url"],
            Route: "/settings/environment",
            ExpandSection: "general",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "environment.smtp",
            Topic: "environment",
            Title: "SMTP / outgoing email",
            Description: "Configure the SMTP relay for password-reset emails, run-failure notifications, and invite emails.",
            Intents: ["smtp", "configure email", "outgoing mail", "email server"],
            Route: "/settings/environment",
            ExpandSection: "smtp",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "environment.ai.anthropic",
            Topic: "environment",
            Title: "Anthropic API key + default Claude model",
            Description: "Save the Anthropic API key (stored at /data/secrets/anthropic.key, never returned by the API) and pick the default Claude model. Required for cloud Claude-powered agents and the in-app assistant when default-provider is set to Anthropic.",
            Intents:
            [
                "anthropic key",
                "claude key",
                "set anthropic",
                "configure anthropic",
                "claude model",
            ],
            Route: "/settings/environment",
            ExpandSection: "aiAnthropic",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "environment.ai.openai",
            Topic: "environment",
            Title: "OpenAI API key + default model",
            Description: "Save the OpenAI API key and default model. Also covers Azure OpenAI via the optional Base URL / Azure deployment fields.",
            Intents:
            [
                "openai key",
                "gpt key",
                "azure openai",
                "set openai",
                "configure gpt",
                "openai model",
            ],
            Route: "/settings/environment",
            ExpandSection: "aiOpenAI",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "environment.ai.local",
            Topic: "environment",
            Title: "Local LLM (Ollama / LM Studio)",
            Description: "Configure an OpenAI-compatible local server — Ollama, LM Studio, vLLM. Quick presets fill in the standard endpoints; the API key field is optional since most local servers don't authenticate.",
            Intents:
            [
                "ollama",
                "lm studio",
                "local model",
                "local llm",
                "self-hosted model",
                "vllm",
            ],
            Route: "/settings/environment",
            ExpandSection: "aiLocal",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        new(
            Id: "environment.ai.default",
            Topic: "environment",
            Title: "Default AI provider",
            Description: "Choose which provider (Anthropic, OpenAI, Local) the in-app assistant and agentic jobs route to when no provider is specified.",
            Intents: ["default ai", "default provider", "which ai"],
            Route: "/settings/environment",
            ExpandSection: "aiProviders",
            RequiresRole: Roles.Admin,
            Mutates: true
        ),
        // ─── Settings → Workspaces ────────────────────────────────────────
        // Migrated to [AiCapability] attributes on WorkspacesEndpoints.List
        // and WorkspacesEndpoints.Create — see EndpointAttributeProvider.
        // ─── Auth ─────────────────────────────────────────────────────────
        new(
            Id: "auth.changePassword",
            Topic: "account",
            Title: "Change my password",
            Description: "Update your account password. Requires the current password.",
            Intents: ["change my password", "update password", "change password", "new password"],
            Route: "/profile",
            RequiresRole: Roles.User,
            Mutates: true
        ),
        new(
            Id: "auth.themeMode",
            Topic: "account",
            Title: "Switch dark / light mode",
            Description: "Toggle the UI between dark, light, and auto (follow system) modes. Per-user preference, persisted in your browser.",
            Intents: ["dark mode", "light mode", "switch theme", "toggle theme"],
            Route: null,
            RequiresRole: Roles.User,
            Mutates: false
        ),
    ];

    public Task<IEnumerable<Capability>> GetAsync(
        CapabilityContext ctx,
        CancellationToken ct = default
    ) => Task.FromResult<IEnumerable<Capability>>(All);
}
