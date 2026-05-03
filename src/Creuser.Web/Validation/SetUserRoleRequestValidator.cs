using Creuser.Auth.Abstractions;
using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class SetUserRoleRequestValidator : AbstractValidator<SetUserRoleRequest>
{
    public SetUserRoleRequestValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty()
            .Must(r => r == Roles.Admin || r == Roles.User)
            .WithMessage($"Role must be '{Roles.Admin}' or '{Roles.User}'.");
    }
}
