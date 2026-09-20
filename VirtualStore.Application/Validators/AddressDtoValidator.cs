using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class AddressDtoValidator : AbstractValidator<AddressDto>
{
    public AddressDtoValidator()
    {
        RuleFor(x => x.Street).NotEmpty().WithMessage("Street is required.").MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().WithMessage("City is required.").MaximumLength(100);
        RuleFor(x => x.State).NotEmpty().WithMessage("State is required.").MaximumLength(100);
        RuleFor(x => x.ZipCode).NotEmpty().WithMessage("ZipCode is required.").MaximumLength(20);
        RuleFor(x => x.Country).NotEmpty().WithMessage("Country is required.").MaximumLength(100);
    }
}
