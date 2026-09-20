using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class UpdateEnterpriseInfoDtoValidator : AbstractValidator<UpdateEnterpriseInfoDto>
{
    public UpdateEnterpriseInfoDtoValidator()
    {
        RuleFor(x => x.CompanyName)
            .NotEmpty().WithMessage("CompanyName must not be empty.")
            .MaximumLength(200)
            .When(x => x.CompanyName != null);

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address must not be empty.")
            .MaximumLength(500)
            .When(x => x.Address != null);

        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Phone must not be empty.")
            .MaximumLength(20)
            .When(x => x.Phone != null);

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email must not be empty.")
            .EmailAddress().WithMessage("Email must be a valid email address.")
            .MaximumLength(256)
            .When(x => x.Email != null);

        RuleFor(x => x.LogoUrl)
            .MaximumLength(2048)
            .Must(BeValidAbsoluteUrl).WithMessage("LogoUrl must be a valid absolute http(s) URL.")
            .When(x => !string.IsNullOrEmpty(x.LogoUrl));

        RuleFor(x => x.AboutUs)
            .MaximumLength(4000)
            .When(x => x.AboutUs != null);

        RuleFor(x => x.TermsAndConditions)
            .MaximumLength(8000)
            .When(x => x.TermsAndConditions != null);

        RuleFor(x => x.PrivacyPolicy)
            .MaximumLength(8000)
            .When(x => x.PrivacyPolicy != null);
    }

    private static bool BeValidAbsoluteUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return true;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
