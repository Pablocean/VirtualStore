namespace VirtualStore.Domain.Enums;

// NOTE: Multi-role membership is modeled as List<UserRole> on User.
// This is NOT a bitmask: do NOT use bitwise ops (|, &, HasFlag).
// Each entry is a discrete role value; membership checks use Contains/Any.
public enum UserRole
{
    Customer = 1,
    Manager = 2,
    Admin = 4
}

/// <summary>
/// Validates discrete multi-role lists (no bitwise semantics).
/// </summary>
public static class UserRoles
{
    /// <summary>
    /// Validates roles: distinct values, all defined in <see cref="UserRole"/>.
    /// Throws if empty or contains undefined values. Returns de-duplicated list.
    /// </summary>
    public static List<UserRole> EnsureValid(IEnumerable<UserRole>? roles)
    {
        if (roles is null)
            throw new ArgumentNullException(nameof(roles));

        var distinct = roles.Distinct().ToList();

        if (distinct.Count == 0)
            throw new ArgumentException("At least one role is required.", nameof(roles));

        foreach (var role in distinct)
        {
            if (!Enum.IsDefined(typeof(UserRole), role))
                throw new ArgumentOutOfRangeException(nameof(roles), role, $"Undefined role value: {(int)role}.");
        }

        return distinct;
    }
}
