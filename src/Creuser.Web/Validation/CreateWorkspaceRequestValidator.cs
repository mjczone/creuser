using Creuser.Core.Repositories;
using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class CreateWorkspaceRequestValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public CreateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Slug)
            .NotEmpty()
            .Matches("^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])?$")
            .WithMessage(
                "Slug must be 2-64 chars, lowercase letters / digits / hyphens, "
                    + "no leading or trailing hyphen."
            );
        RuleFor(x => x.Name).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Description!).MaximumLength(1024).When(x => x.Description is not null);

        RuleFor(x => x.Type)
            .NotEmpty()
            .Must(t => t == WorkspaceType.Git || t == WorkspaceType.Local)
            .WithMessage("Type must be 'git' or 'local'. ('s3' is reserved for a future release.)");

        // Type-specific settings: exactly one must be populated and it must
        // match the Type field.
        RuleFor(x => x.GitSettings)
            .NotNull()
            .When(x => x.Type == WorkspaceType.Git)
            .WithMessage("Git workspaces require gitSettings.");
        RuleFor(x => x.LocalSettings)
            .NotNull()
            .When(x => x.Type == WorkspaceType.Local)
            .WithMessage("Local workspaces require localSettings.");

        // Git rules — only validated when the matching settings is supplied.
        When(
            x => x.Type == WorkspaceType.Git && x.GitSettings is not null,
            () =>
            {
                RuleFor(x => x.GitSettings!.RepositoryUrl).NotEmpty().MaximumLength(1024);
                RuleFor(x => x.GitSettings!.WorkingBranch).NotEmpty().MaximumLength(255);
                RuleFor(x => x.GitSettings!.SourceBranch).NotEmpty().MaximumLength(255);
                RuleFor(x => x.GitSettings!.Mode)
                    .Must(m =>
                        m == GitWorkspaceMode.DirectPush || m == GitWorkspaceMode.PullRequest
                    )
                    .WithMessage(
                        $"Mode must be '{GitWorkspaceMode.DirectPush}' or '{GitWorkspaceMode.PullRequest}'."
                    );
                RuleFor(x => x.GitSettings!.PushFrequency)
                    .Must(p =>
                        p == GitWorkspacePushFrequency.EveryCommit
                        || p == GitWorkspacePushFrequency.OnDemand
                    )
                    .WithMessage(
                        $"Push frequency must be '{GitWorkspacePushFrequency.EveryCommit}' or '{GitWorkspacePushFrequency.OnDemand}'."
                    );
                RuleFor(x => x.GitSettings!.AuthMode)
                    .Must(GitAuthMode.IsValid)
                    .WithMessage("Auth mode must be 'none', 'https-pat', or 'ssh-key'.");
                RuleFor(x => x.GitSettings!.AuthCredential)
                    .NotEmpty()
                    .When(x => x.GitSettings!.AuthMode != GitAuthMode.None)
                    .WithMessage(x =>
                        x.GitSettings!.AuthMode == GitAuthMode.HttpsPat
                            ? "Personal Access Token is required for HTTPS authentication."
                            : "Private key is required for SSH authentication."
                    );
            }
        );

        // Local rules.
        When(
            x => x.Type == WorkspaceType.Local && x.LocalSettings is not null,
            () =>
            {
                RuleFor(x => x.LocalSettings!.Path)
                    .NotEmpty()
                    .MaximumLength(4096)
                    .Must(p => System.IO.Path.IsPathRooted(p))
                    .WithMessage("Path must be an absolute filesystem path.");
            }
        );
    }
}
