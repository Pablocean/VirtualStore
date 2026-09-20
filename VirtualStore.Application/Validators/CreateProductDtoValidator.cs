using FluentValidation;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Validators;

public sealed class CreateProductDtoValidator : AbstractValidator<CreateProductDto>
{
    public CreateProductDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Description)
            .MaximumLength(2000);

        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price must be >= 0.");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-letter code.")
            .Matches("^[A-Za-z]{3}$").WithMessage("Currency must be a 3-letter code.");

        RuleFor(x => x.StockQuantity)
            .GreaterThanOrEqualTo(0).WithMessage("Stock must be >= 0.");

        RuleFor(x => x.CategoryId)
            .NotEmpty().WithMessage("CategoryId is required.");
    }
}
