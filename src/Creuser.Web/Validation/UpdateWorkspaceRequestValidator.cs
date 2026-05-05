using Creuser.Core.Repositories;
using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class UpdateWorkspaceRequestValidator : AbstractValidator<UpdateWorkspaceRequest>
{
    public UpdateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Description!).MaximumLength(1024).When(x => x.Description is not null);

        RuleFor(x => x.Type)
            .NotEmpty()
            .Must(t => t == WorkspaceType.Git || t == WorkspaceType.Local)
            .WithMessage("Type must be 'git' or 'local'.");

        RuleFor(x => x.GitSettings)
            .NotNull()
            .When(x => x.Type == WorkspaceType.Git)
            .WithMessage("Git workspaces require gitSettings.");
        RuleFor(x => x.LocalSettings)
            .NotNull()
            .When(x => x.Type == WorkspaceType.Local)
            .WithMessage("Local workspaces require localSettings.");

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
                // Update accepts a null AuthCredential (rotation skipped) — the
                // existing on-disk secret is reused. Only validate when an
                // explicit credential is supplied.
            }
        );

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
