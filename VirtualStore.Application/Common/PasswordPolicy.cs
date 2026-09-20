using FluentValidation;
using FluentValidation.Results;

namespace VirtualStore.Application.Common;

/// <summary>
/// Shared password rule (single source of truth, mirrors CreateUserDtoValidator):
/// min 8 chars, at least one upper-case, one lower-case, one digit.
/// Validators encode the same rule declaratively for 400 responses; services call
/// <see cref="EnsureValid"/> as defense-in-depth for non-HTTP call paths.
/// </summary>
public static class PasswordPolicy
{
    public static void EnsureValid(string password, string propertyName = "NewPassword")
    {
        var failures = Validate(password, propertyName);
        if (failures.Count > 0)
            throw new ValidationException(failures);
    }

    public static List<ValidationFailure> Validate(string password, string propertyName = "NewPassword")
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrEmpty(password))
        {
            failures.Add(new ValidationFailure(propertyName, "Password is required."));
            return failures;
        }

        if (password.Length < 8)
            failures.Add(new ValidationFailure(propertyName, "Password must be at least 8 characters."));
        if (!password.Any(char.IsUpper))
            failures.Add(new ValidationFailure(propertyName, "Password must contain at least one uppercase letter."));
        if (!password.Any(char.IsLower))
            failures.Add(new ValidationFailure(propertyName, "Password must contain at least one lowercase letter."));
        if (!password.Any(char.IsDigit))
            failures.Add(new ValidationFailure(propertyName, "Password must contain at least one digit."));
        return failures;
    }
}
