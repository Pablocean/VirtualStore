using FluentValidation;
using VirtualStore.Application.DTOs.Auth;

namespace VirtualStore.Application.Validators;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Email must be a valid email address.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");

        When(x => !string.IsNullOrEmpty(x.OtpCode), () =>
        {
            RuleFor(x => x.OtpCode!)
                .Length(6).WithMessage("OTP code must be 6 characters.")
                .Matches("^[0-9]+$").WithMessage("OTP code must contain only digits.");
        });
    }
}
