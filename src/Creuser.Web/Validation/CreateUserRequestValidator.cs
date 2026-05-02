using Creuser.Auth.Abstractions;
using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Role)
            .NotEmpty()
            .Must(r => r == Roles.Admin || r == Roles.User)
            .WithMessage($"Role must be '{Roles.Admin}' or '{Roles.User}'.");
        RuleFor(x => x.TemporaryPassword!)
            .MinimumLength(8)
            .WithMessage("Temporary password must be at least 8 characters when provided.")
            .When(x => !string.IsNullOrEmpty(x.TemporaryPassword));
    }
}
