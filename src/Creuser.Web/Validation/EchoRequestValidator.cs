using Creuser.Web.Contracts.Requests;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator()
    {
        RuleFor(x => x.Message)
            .NotEmpty()
            .WithMessage("Message is required.")
            .MaximumLength(500)
            .WithMessage("Message must be 500 characters or fewer.");

        RuleFor(x => x.Repeat)
            .InclusiveBetween(1, 10)
            .When(x => x.Repeat is not null)
            .WithMessage("Repeat must be between 1 and 10.");
    }
}
