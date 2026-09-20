using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class CreateOrderDtoValidator : AbstractValidator<CreateOrderDto>
{
    public CreateOrderDtoValidator()
    {
        RuleFor(x => x.Items)
            .NotNull().WithMessage("Items are required.")
            .NotEmpty().WithMessage("Order must contain at least one item.");

        RuleForEach(x => x.Items).SetValidator(new OrderItemDtoValidator());

        RuleFor(x => x.ShippingAddress)
            .NotNull().WithMessage("ShippingAddress is required.")
            .SetValidator(new AddressDtoValidator());
    }
}
