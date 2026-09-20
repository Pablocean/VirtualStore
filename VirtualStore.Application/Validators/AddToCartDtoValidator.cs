using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class AddToCartDtoValidator : AbstractValidator<AddToCartDto>
{
    public AddToCartDtoValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("ProductId is required.");

        RuleFor(x => x.Quantity)
            .InclusiveBetween(1, 99).WithMessage("Quantity must be between 1 and 99.");
    }
}
