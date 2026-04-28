using AutoMapper;
using VirtualStore.Application.DTOs;
using VirtualStore.Domain.Entities;

namespace VirtualStore.Application.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // ==================== USER ====================
        CreateMap<User, UserDto>().ReverseMap();

        CreateMap<CreateUserDto, User>()
            .ForMember(dest => dest.PasswordHash, opt => opt.Ignore())
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.IsDeleted, opt => opt.Ignore())
            .ForMember(dest => dest.EmailConfirmed, opt => opt.Ignore())
            .ForMember(dest => dest.TwoFactorEnabled, opt => opt.Ignore())
            .ForMember(dest => dest.TwoFactorSecret, opt => opt.Ignore())
            .ForMember(dest => dest.LastLoginAt, opt => opt.Ignore())
            .ForMember(dest => dest.RefreshTokens, opt => opt.Ignore());

        // UpdateUserDto: apply condition to all members, then ignore immutable/base properties
        var updateUserMap = CreateMap<UpdateUserDto, User>();
        updateUserMap.ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));
        updateUserMap.ForMember(dest => dest.PasswordHash, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.Email, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.Id, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.CreatedAt, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.UpdatedAt, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.IsDeleted, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.TwoFactorSecret, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.LastLoginAt, opt => opt.Ignore());
        updateUserMap.ForMember(dest => dest.RefreshTokens, opt => opt.Ignore());

        // ==================== PRODUCT ====================
        CreateMap<Product, ProductDto>()
            .ForMember(dest => dest.CategoryName, opt => opt.Ignore());

        CreateMap<CreateProductDto, Product>()
            .ForMember(dest => dest.IsActive, opt => opt.Ignore())
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.IsDeleted, opt => opt.Ignore());

        var updateProductMap = CreateMap<UpdateProductDto, Product>();
        updateProductMap.ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));
        updateProductMap.ForMember(dest => dest.Id, opt => opt.Ignore());
        updateProductMap.ForMember(dest => dest.CreatedAt, opt => opt.Ignore());
        updateProductMap.ForMember(dest => dest.UpdatedAt, opt => opt.Ignore());
        updateProductMap.ForMember(dest => dest.IsDeleted, opt => opt.Ignore());

        // ==================== CATEGORY ====================
        CreateMap<Category, CategoryDto>().ReverseMap();

        CreateMap<CreateCategoryDto, Category>()
            .ForMember(dest => dest.IsActive, opt => opt.Ignore())
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.IsDeleted, opt => opt.Ignore());

        var updateCategoryMap = CreateMap<UpdateCategoryDto, Category>();
        updateCategoryMap.ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));
        updateCategoryMap.ForMember(dest => dest.Id, opt => opt.Ignore());
        updateCategoryMap.ForMember(dest => dest.CreatedAt, opt => opt.Ignore());
        updateCategoryMap.ForMember(dest => dest.UpdatedAt, opt => opt.Ignore());
        updateCategoryMap.ForMember(dest => dest.IsDeleted, opt => opt.Ignore());

        // ==================== CART ====================
        CreateMap<Cart, CartDto>();
        CreateMap<CartItem, CartItemDto>().ReverseMap();

        // ==================== ORDER ====================
        CreateMap<Order, OrderDto>();
        CreateMap<OrderItem, OrderItemDto>().ReverseMap();
        CreateMap<Address, AddressDto>().ReverseMap();

        CreateMap<CreateOrderDto, Order>()
            .ForMember(dest => dest.UserId, opt => opt.Ignore())
            .ForMember(dest => dest.TotalAmount, opt => opt.Ignore())
            .ForMember(dest => dest.Currency, opt => opt.Ignore())
            .ForMember(dest => dest.Status, opt => opt.Ignore())
            .ForMember(dest => dest.StripePaymentIntentId, opt => opt.Ignore())
            .ForMember(dest => dest.Id, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
            .ForMember(dest => dest.IsDeleted, opt => opt.Ignore());

        // ==================== ENTERPRISE INFO ====================
        CreateMap<EnterpriseInfo, EnterpriseInfoDto>();

        var updateInfoMap = CreateMap<UpdateEnterpriseInfoDto, EnterpriseInfo>();
        updateInfoMap.ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));
        updateInfoMap.ForMember(dest => dest.Id, opt => opt.Ignore());
        updateInfoMap.ForMember(dest => dest.CreatedAt, opt => opt.Ignore());
        updateInfoMap.ForMember(dest => dest.UpdatedAt, opt => opt.Ignore());
        updateInfoMap.ForMember(dest => dest.IsDeleted, opt => opt.Ignore());
    }
}