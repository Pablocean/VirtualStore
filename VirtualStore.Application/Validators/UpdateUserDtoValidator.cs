using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class UpdateUserDtoValidator : AbstractValidator<UpdateUserDto>
{
    public UpdateUserDtoValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username must not be empty.")
            .MinimumLength(3).WithMessage("Username must be at least 3 characters.")
            .MaximumLength(50)
            .When(x => x.Username != null);

        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("FirstName must not be empty.")
            .MaximumLength(100)
            .When(x => x.FirstName != null);

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("LastName must not be empty.")
            .MaximumLength(100)
            .When(x => x.LastName != null);

        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("PhoneNumber must not be empty.")
            .MaximumLength(20)
            .When(x => x.PhoneNumber != null);
    }
}
