using Creuser.Core.Execution;
using Creuser.Web.Contracts.Requests;
using Creuser.Web.Schedules;
using FluentValidation;

namespace Creuser.Web.Validation;

public sealed class CreateScheduleRequestValidator : AbstractValidator<CreateScheduleRequest>
{
    public CreateScheduleRequestValidator()
    {
        RuleFor(x => x.JobScriptId).NotEmpty();
        RuleFor(x => x.Kind)
            .Must(ScheduleKind.IsValid)
            .WithMessage("Kind must be one of: cron, sync.");

        // Cron expression required + parseable when kind is cron;
        // forbidden when kind is sync.
        When(
            x => string.Equals(x.Kind, ScheduleKind.Cron, StringComparison.Ordinal),
            () =>
            {
                RuleFor(x => x.CronExpression)
                    .NotEmpty()
                    .WithMessage("Cron schedules require a non-empty `cronExpression`.");
                RuleFor(x => x.CronExpression)
                    .Must(expr => CronEvaluator.TryParse(expr, out _))
                    .When(x => !string.IsNullOrEmpty(x.CronExpression))
                    .WithMessage(
                        "Cron expression isn't valid (NCrontab couldn't parse it). Use 5 fields `m h dom mon dow` or 6 with seconds."
                    );
            }
        );
        When(
            x => string.Equals(x.Kind, ScheduleKind.Sync, StringComparison.Ordinal),
            () =>
            {
                RuleFor(x => x.CronExpression)
                    .Empty()
                    .WithMessage("Sync schedules don't take a cron expression.");
            }
        );
    }
}

public sealed class UpdateScheduleRequestValidator : AbstractValidator<UpdateScheduleRequest>
{
    public UpdateScheduleRequestValidator()
    {
        RuleFor(x => x.Kind).Must(ScheduleKind.IsValid);
        When(
            x => string.Equals(x.Kind, ScheduleKind.Cron, StringComparison.Ordinal),
            () =>
            {
                RuleFor(x => x.CronExpression)
                    .NotEmpty()
                    .Must(expr => CronEvaluator.TryParse(expr, out _))
                    .When(x => !string.IsNullOrEmpty(x.CronExpression))
                    .WithMessage("Cron expression isn't valid.");
            }
        );
        When(
            x => string.Equals(x.Kind, ScheduleKind.Sync, StringComparison.Ordinal),
            () =>
            {
                RuleFor(x => x.CronExpression).Empty();
            }
        );
    }
}
