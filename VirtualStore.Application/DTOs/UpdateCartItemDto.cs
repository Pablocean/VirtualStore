namespace VirtualStore.Application.DTOs;

/// <summary>Payload for updating a cart item quantity.</summary>
public class UpdateCartItemDto
{
    public int Quantity { get; set; } = 1;
}
