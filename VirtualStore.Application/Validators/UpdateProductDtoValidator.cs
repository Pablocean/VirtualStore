using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class UpdateProductDtoValidator : AbstractValidator<UpdateProductDto>
{
    public UpdateProductDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name must not be empty.")
            .MaximumLength(200)
            .When(x => x.Name != null);

        RuleFor(x => x.Description)
            .MaximumLength(2000)
            .When(x => x.Description != null);

        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price must be >= 0.")
            .When(x => x.Price.HasValue);

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency must not be empty.")
            .Length(3).WithMessage("Currency must be a 3-letter code.")
            .Matches("^[A-Za-z]{3}$").WithMessage("Currency must be a 3-letter code.")
            .When(x => x.Currency != null);

        RuleFor(x => x.StockQuantity)
            .GreaterThanOrEqualTo(0).WithMessage("Stock must be >= 0.")
            .When(x => x.StockQuantity.HasValue);

        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("CategoryId must not be empty.")
            .When(x => x.CategoryId != null);
    }
}
