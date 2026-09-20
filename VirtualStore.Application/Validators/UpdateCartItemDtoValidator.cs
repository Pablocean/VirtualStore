using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class UpdateCartItemDtoValidator : AbstractValidator<UpdateCartItemDto>
{
    public UpdateCartItemDtoValidator()
    {
        RuleFor(x => x.Quantity)
            .InclusiveBetween(1, 99).WithMessage("Quantity must be between 1 and 99.");
    }
}
