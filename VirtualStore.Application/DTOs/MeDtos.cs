namespace VirtualStore.Application.DTOs;

/// <summary>
/// GDPR export composite (GET /api/me/export, ADR-0010). Carries no secrets:
/// no password hash, no refresh-token values, no replacement-token values.
/// </summary>
public class MeExportDto
{
    public UserDto Profile { get; set; } = new();
    public List<OrderDto> Orders { get; set; } = new();
    public List<CartDto> Carts { get; set; } = new();
    public List<RefreshTokenMetadataDto> TokenMetadata { get; set; } = new();
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Refresh-token metadata WITHOUT token secrets (<c>Token</c> and
/// <c>ReplacedByToken</c> values are never exported).
/// </summary>
public class RefreshTokenMetadataDto
{
    public DateTime Created { get; set; }
    public string CreatedByIp { get; set; } = string.Empty;
    public DateTime Expires { get; set; }
    public DateTime? Revoked { get; set; }
    public string? RevokedByIp { get; set; }
    public bool HasReplacement { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// GDPR erasure request (POST /api/me/purge). The current password must be
/// supplied to confirm intent; it is verified with BCrypt and never logged.
/// </summary>
public class PurgeRequestDto
{
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// GDPR erasure summary (POST /api/me/purge → 200).
/// </summary>
public class PurgeResultDto
{
    public bool UserDeleted { get; set; }
    public int CartsDeleted { get; set; }
    public int OrdersPseudonymized { get; set; }
}
