using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class OrderItemDtoValidator : AbstractValidator<OrderItemDto>
{
    public OrderItemDtoValidator()
    {
        RuleFor(x => x.ProductId)
            .NotEmpty().WithMessage("ProductId is required.");

        RuleFor(x => x.Quantity)
            .InclusiveBetween(1, 99).WithMessage("Quantity must be between 1 and 99.");

        RuleFor(x => x.UnitPrice)
            .GreaterThanOrEqualTo(0).WithMessage("UnitPrice must be >= 0.");
    }
}
