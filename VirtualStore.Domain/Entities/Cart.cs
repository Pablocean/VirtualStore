namespace VirtualStore.Domain.Entities;

public class Cart : BaseEntity
{
    public string UserId { get; set; } = string.Empty;      // Anonymous users based on session? For logged-in use UserId
    public List<CartItem> Items { get; set; } = new();
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
}