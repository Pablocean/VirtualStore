using FluentValidation;
using VirtualStore.Application.DTOs.Auth;

namespace VirtualStore.Application.Validators;

public sealed class ResendConfirmationDtoValidator : AbstractValidator<ResendConfirmationDto>
{
    public ResendConfirmationDtoValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Email must be a valid email address.");
    }
}
