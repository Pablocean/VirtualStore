using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface ICartService
{
    Task<CartDto> GetCartAsync(string userId);
    Task<CartDto> AddToCartAsync(string userId, AddToCartDto dto);
    Task<CartDto> UpdateCartItemAsync(string userId, string productId, int quantity);
    Task<CartDto> RemoveFromCartAsync(string userId, string productId);
    Task ClearCartAsync(string userId);
}