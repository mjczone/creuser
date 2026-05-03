using Creuser.Core.Execution;
using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class CreateJobScriptRequestValidator : AbstractValidator<CreateJobScriptRequest>
{
    public CreateJobScriptRequestValidator()
    {
        RuleFor(x => x.Slug)
            .NotEmpty()
            .Matches("^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])?$")
            .WithMessage("Lowercase letters, digits, hyphens. No leading or trailing hyphen.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Description).MaximumLength(1024);
        RuleFor(x => x.Pattern)
            .Must(JobPattern.IsValid)
            .WithMessage("Pattern must be one of: deterministic, plan-then-execute, agentic.");
        RuleFor(x => x.Status)
            .Must(JobScriptStatus.IsValid)
            .WithMessage("Status must be one of: draft, active, disabled.");
    }
}

public sealed class UpdateJobScriptRequestValidator : AbstractValidator<UpdateJobScriptRequest>
{
    public UpdateJobScriptRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Description).MaximumLength(1024);
        RuleFor(x => x.Pattern).Must(JobPattern.IsValid);
        RuleFor(x => x.Status).Must(JobScriptStatus.IsValid);
    }
}
