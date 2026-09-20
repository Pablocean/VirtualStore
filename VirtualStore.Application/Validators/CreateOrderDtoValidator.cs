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

        // Optional idempotency key (ADR-0006): bound length only, so keys stay
        // index-friendly. Absent/blank keys mean "no idempotency".
        RuleFor(x => x.IdempotencyKey)
            .MaximumLength(100).WithMessage("IdempotencyKey must be at most 100 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.IdempotencyKey));
    }
}
