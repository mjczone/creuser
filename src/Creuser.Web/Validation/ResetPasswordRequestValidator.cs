using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.TemporaryPassword!)
            .MinimumLength(8)
            .WithMessage("Temporary password must be at least 8 characters when provided.")
            .When(x => !string.IsNullOrEmpty(x.TemporaryPassword));
    }
}
