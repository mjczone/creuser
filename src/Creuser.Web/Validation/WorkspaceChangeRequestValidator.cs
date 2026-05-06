using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class WorkspaceChangeRequestValidator : AbstractValidator<WorkspaceChangeRequest>
{
    public WorkspaceChangeRequestValidator()
    {
        RuleFor(x => x.Changes).NotEmpty().WithMessage("At least one file change is required.");

        // Per-change validation. Path safety is critical because the
        // endpoint resolves paths inside the workspace working surface;
        // a `..` segment would escape it. The validator handles
        // surface-level rejection; the providers re-validate the resolved
        // absolute path stays under the working root as defense-in-depth.
        RuleForEach(x => x.Changes)
            .ChildRules(change =>
            {
                change
                    .RuleFor(c => c.Path)
                    .NotEmpty()
                    .WithMessage("Each change must have a path.")
                    .MaximumLength(1024)
                    .Must(IsSafeRelativePath)
                    .WithMessage(
                        "Path must be relative, must not start with '/' or contain '..' segments, "
                            + "and must not target the workspace's `.git/` directory."
                    );

                change
                    .RuleFor(c => c.Action)
                    .Must(a => a == "write" || a == "delete")
                    .WithMessage("Action must be 'write' or 'delete'.");

                change
                    .RuleFor(c => c.Content)
                    .NotNull()
                    .When(c => c.Action == "write")
                    .WithMessage("Content is required for 'write' actions.");
            });
    }

    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 2 && path[1] == ':'))
            return false;
        var segments = path.Split('/', '\\');
        foreach (var seg in segments)
        {
            if (seg == ".." || seg == ".")
                return false;
            if (string.Equals(seg, ".git", System.StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}

public sealed class WorkspaceCommitRequestValidator : AbstractValidator<WorkspaceCommitRequest>
{
    public WorkspaceCommitRequestValidator()
    {
        RuleFor(x => x.CommitMessage)
            .NotEmpty()
            .MaximumLength(1024)
            .WithMessage("Commit message is required and must be under 1024 characters.");
    }
}
