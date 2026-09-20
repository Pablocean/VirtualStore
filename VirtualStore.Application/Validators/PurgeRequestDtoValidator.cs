using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

/// <summary>
/// POST /api/me/purge confirmation (ADR-0010). Only requires the current
/// password — strength rules do not apply to a confirmation field.
/// </summary>
public sealed class PurgeRequestDtoValidator : AbstractValidator<PurgeRequestDto>
{
    public PurgeRequestDtoValidator()
    {
        RuleFor(x => x.ConfirmPassword)
            .NotEmpty().WithMessage("Current password is required.");
    }
}
