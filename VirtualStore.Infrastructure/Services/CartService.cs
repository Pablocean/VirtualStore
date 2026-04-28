using AutoMapper;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class CartService : ICartService
{
    private readonly IRepository<Cart> _cartRepo;
    private readonly IRepository<Product> _productRepo;
    private readonly IMapper _mapper;

    public CartService(IRepository<Cart> cartRepo, IRepository<Product> productRepo, IMapper mapper)
    {
        _cartRepo = cartRepo;
        _productRepo = productRepo;
        _mapper = mapper;
    }

    public async Task<CartDto> GetCartAsync(string userId)
    {
        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId) ?? new Cart { UserId = userId };
        return _mapper.Map<CartDto>(cart);
    }

    public async Task<CartDto> AddToCartAsync(string userId, AddToCartDto dto)
    {
        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId) ?? new Cart { UserId = userId };
        var product = await _productRepo.GetByIdAsync(dto.ProductId) ?? throw new KeyNotFoundException("Product not found");

        var existingItem = cart.Items.Find(i => i.ProductId == dto.ProductId);
        if (existingItem != null)
            existingItem.Quantity += dto.Quantity;
        else
            cart.Items.Add(new CartItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = dto.Quantity
            });

        if (cart.Id == null)
            await _cartRepo.AddAsync(cart);
        else
            await _cartRepo.UpdateAsync(cart.Id, cart);

        return _mapper.Map<CartDto>(cart);
    }

    public async Task<CartDto> UpdateCartItemAsync(string userId, string productId, int quantity)
    {
        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId) ?? throw new InvalidOperationException("Cart not found");
        var item = cart.Items.Find(i => i.ProductId == productId) ?? throw new KeyNotFoundException("Product not in cart");
        item.Quantity = quantity;
        await _cartRepo.UpdateAsync(cart.Id, cart);
        return _mapper.Map<CartDto>(cart);
    }

    public async Task<CartDto> RemoveFromCartAsync(string userId, string productId)
    {
        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId) ?? throw new InvalidOperationException("Cart not found");
        cart.Items.RemoveAll(i => i.ProductId == productId);
        await _cartRepo.UpdateAsync(cart.Id, cart);
        return _mapper.Map<CartDto>(cart);
    }

    public async Task ClearCartAsync(string userId)
    {
        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId);
        if (cart != null)
            await _cartRepo.DeleteAsync(cart.Id);
    }
}