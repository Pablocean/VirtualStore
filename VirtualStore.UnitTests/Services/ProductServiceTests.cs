using System.Linq.Expressions;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class ProductServiceTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private sealed record Harness(
        ProductService Service,
        Mock<IRepository<Product>> Products,
        Mock<IRepository<Category>> Categories,
        Mock<ICacheService> Cache);

    private static Harness Build(Dictionary<string, Product>? products = null)
    {
        products ??= new Dictionary<string, Product>();
        var productRepo = new Mock<IRepository<Product>>();
        var categoryRepo = new Mock<IRepository<Category>>();
        var cache = new Mock<ICacheService>();

        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => products.TryGetValue(id, out var p) ? p : null);
        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => products.TryGetValue(id, out var p) ? p : null);

        return new Harness(new ProductService(productRepo.Object, categoryRepo.Object, RealMapper(), cache.Object),
            productRepo, categoryRepo, cache);
    }

    private static void SetupCacheMiss(Harness h)
    {
        ProductDto? miss = null;
        h.Cache.Setup(c => c.TryGet<ProductDto>(It.IsAny<string>(), out miss)).Returns(false);
    }

    private static void SetupCacheHit(Harness h, ProductDto dto)
    {
        ProductDto? hit = dto;
        h.Cache.Setup(c => c.TryGet<ProductDto>(It.IsAny<string>(), out hit)).Returns(true);
    }

    private static Product P(string id, string name = "Widget", decimal price = 10m,
        string categoryId = "c1", bool active = true) => new()
    {
        Id = id,
        Name = name,
        Description = "d",
        Price = price,
        Currency = "usd",
        StockQuantity = 5,
        CategoryId = categoryId,
        IsActive = active
    };

    // ---------- GetProductByIdAsync ----------

    [Fact]
    public async Task GetById_Cache_Hit_Returns_Cached_Without_Repo()
    {
        var h = Build();
        var cached = new ProductDto { Id = "p1", Name = "Cached" };
        SetupCacheHit(h, cached);

        var result = await h.Service.GetProductByIdAsync("p1");

        result.Should().BeSameAs(cached);
        h.Products.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
        h.Cache.Verify(c => c.Set(It.IsAny<string>(), It.IsAny<ProductDto>()), Times.Never);
    }

    [Fact]
    public async Task GetById_TryGet_True_But_Null_Falls_Through_To_Repo()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1") });
        ProductDto? nullDto = null;
        h.Cache.Setup(c => c.TryGet<ProductDto>(It.IsAny<string>(), out nullDto)).Returns(true);

        var result = await h.Service.GetProductByIdAsync("p1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("p1");
        h.Products.Verify(r => r.GetByIdAsync("p1"), Times.Once);
    }

    [Fact]
    public async Task GetById_Miss_Product_Null_Returns_Null_Without_Caching()
    {
        var h = Build();
        SetupCacheMiss(h);

        var result = await h.Service.GetProductByIdAsync("ghost");

        result.Should().BeNull();
        h.Cache.Verify(c => c.Set(It.IsAny<string>(), It.IsAny<ProductDto>()), Times.Never);
    }

    [Fact]
    public async Task GetById_Null_CategoryId_Skips_Category_Lookup()
    {
        var product = P("p1");
        product.CategoryId = null!;
        var h = Build(new Dictionary<string, Product> { ["p1"] = product });
        SetupCacheMiss(h);

        var result = await h.Service.GetProductByIdAsync("p1");

        result.Should().NotBeNull();
        result!.CategoryName.Should().BeNull();
        h.Categories.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
        h.Cache.Verify(c => c.Set("product:p1", It.IsAny<ProductDto>()), Times.Once);
    }

    [Fact]
    public async Task GetById_Empty_CategoryId_Skips_Category_Lookup()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", categoryId: string.Empty) });
        SetupCacheMiss(h);

        var result = await h.Service.GetProductByIdAsync("p1");

        result!.CategoryName.Should().BeNull();
        h.Categories.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetById_Category_Set_But_Missing_Leaves_Name_Null()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", categoryId: "c9") });
        SetupCacheMiss(h);
        h.Categories.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync((Category?)null);

        var result = await h.Service.GetProductByIdAsync("p1");

        result!.CategoryName.Should().BeNull();
        h.Categories.Verify(r => r.GetByIdAsync("c9"), Times.Once);
    }

    [Fact]
    public async Task GetById_Category_Set_And_Found_Hydrates_Name()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", categoryId: "c1") });
        SetupCacheMiss(h);
        h.Categories.Setup(r => r.GetByIdAsync("c1")).ReturnsAsync(new Category { Id = "c1", Name = "Gadgets" });

        var result = await h.Service.GetProductByIdAsync("p1");

        result!.CategoryName.Should().Be("Gadgets");
    }

    // ---------- GetProductsAsync ----------

    private static Harness BuildPaged(
        List<Product> items,
        Dictionary<string, string>? categoryNames = null,
        Action<Expression<Func<Product, bool>>, int, int>? capture = null)
    {
        var h = Build();
        h.Products.Setup(r => r.PagedAsync(
                It.IsAny<Expression<Func<Product, bool>>>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Product, bool>> pred, int page, int size,
                string? sort, bool desc, CancellationToken _) =>
            {
                capture?.Invoke(pred, page, size);
                return ((IReadOnlyList<Product>)items, (long)items.Count);
            });

        categoryNames ??= new Dictionary<string, string>();
        h.Categories.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Category, bool>>>()))
            .ReturnsAsync((Expression<Func<Category, bool>> pred) =>
                categoryNames
                    .Select(kv => new Category { Id = kv.Key, Name = kv.Value })
                    .Where(pred.Compile())
                    .ToList());
        h.Categories.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Category, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Category, bool>> pred, CancellationToken _) =>
                categoryNames
                    .Select(kv => new Category { Id = kv.Key, Name = kv.Value })
                    .Where(pred.Compile())
                    .ToList());
        return h;
    }

    [Fact]
    public async Task GetProducts_Page_Clamps_Below_One_To_One()
    {
        int seenPage = 0, seenSize = 0;
        var h = BuildPaged(new List<Product>(), capture: (_, page, size) => { seenPage = page; seenSize = size; });

        var result = await h.Service.GetProductsAsync(new ProductFilterDto { PageNumber = 0, PageSize = 20, IsActive = null });

        seenPage.Should().Be(1);
        result.PageNumber.Should().Be(1);
    }

    [Fact]
    public async Task GetProducts_Size_Clamps_Zero_To_20_And_Caps_At_100()
    {
        int seenSize = 0;
        var h = BuildPaged(new List<Product>(), capture: (_, _, size) => seenSize = size);

        await h.Service.GetProductsAsync(new ProductFilterDto { PageNumber = 1, PageSize = 0, IsActive = null });
        seenSize.Should().Be(20);

        await h.Service.GetProductsAsync(new ProductFilterDto { PageNumber = 1, PageSize = 500, IsActive = null });
        seenSize.Should().Be(100);
    }

    [Fact]
    public async Task GetProducts_No_Categories_Skips_FindAsync()
    {
        var h = BuildPaged(new List<Product> { P("p1", categoryId: string.Empty) });

        var result = await h.Service.GetProductsAsync(new ProductFilterDto { IsActive = null });

        result.Items.Should().HaveCount(1);
        h.Categories.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Category, bool>>>()), Times.Never);
        h.Categories.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Category, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetProducts_With_Categories_Hydrates_Names_In_One_Query()
    {
        var h = BuildPaged(
            new List<Product> { P("p1", categoryId: "c1"), P("p2", categoryId: "c2"), P("p3", categoryId: "missing") },
            new Dictionary<string, string> { ["c1"] = "Gadgets", ["c2"] = "Tools" });

        var result = await h.Service.GetProductsAsync(new ProductFilterDto { IsActive = null });

        result.Items.Should().HaveCount(3);
        result.Items.Single(i => i.Id == "p1").CategoryName.Should().Be("Gadgets");
        result.Items.Single(i => i.Id == "p2").CategoryName.Should().Be("Tools");
        result.Items.Single(i => i.Id == "p3").CategoryName.Should().BeNull();
        h.Categories.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Category, bool>>>()), Times.Once);
    }

    private static async Task<Func<Product, bool>> CapturePredicate(ProductFilterDto filter)
    {
        Expression<Func<Product, bool>>? captured = null;
        var h = BuildPaged(new List<Product>(), capture: (pred, _, _) => captured = pred);
        await h.Service.GetProductsAsync(filter);
        captured.Should().NotBeNull();
        return captured!.Compile();
    }

    [Fact]
    public async Task Predicate_No_Filters_Matches_All()
    {
        var pred = await CapturePredicate(new ProductFilterDto { IsActive = null });

        pred(P("a", name: "Anything", price: 1m, categoryId: "x")).Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_Search_Matches_Case_Insensitive_Substring()
    {
        var pred = await CapturePredicate(new ProductFilterDto { Search = "  WIDG  ", IsActive = null });

        pred(P("a", name: "Blue Widget Pro")).Should().BeTrue();
        pred(P("b", name: "Gadget")).Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_Whitespace_Search_Is_Ignored()
    {
        var pred = await CapturePredicate(new ProductFilterDto { Search = "   ", IsActive = null });

        pred(P("a", name: "Anything")).Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_CategoryId_Filters()
    {
        var pred = await CapturePredicate(new ProductFilterDto { CategoryId = "c1", IsActive = null });

        pred(P("a", categoryId: "c1")).Should().BeTrue();
        pred(P("b", categoryId: "c2")).Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_MinPrice_Filters()
    {
        var pred = await CapturePredicate(new ProductFilterDto { MinPrice = 10m, IsActive = null });

        pred(P("a", price: 10m)).Should().BeTrue();
        pred(P("b", price: 9.99m)).Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_MaxPrice_Filters()
    {
        var pred = await CapturePredicate(new ProductFilterDto { MaxPrice = 10m, IsActive = null });

        pred(P("a", price: 10m)).Should().BeTrue();
        pred(P("b", price: 10.01m)).Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_IsActive_Filters()
    {
        var pred = await CapturePredicate(new ProductFilterDto { IsActive = true });

        pred(P("a", active: true)).Should().BeTrue();
        pred(P("b", active: false)).Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_Multi_Filter_Requires_All_AndAlso()
    {
        var pred = await CapturePredicate(new ProductFilterDto
        {
            Search = "widget",
            CategoryId = "c1",
            MinPrice = 5m,
            MaxPrice = 20m,
            IsActive = true
        });

        pred(P("ok", name: "Widget", price: 10m, categoryId: "c1", active: true)).Should().BeTrue();
        pred(P("bad-cat", name: "Widget", price: 10m, categoryId: "c2", active: true)).Should().BeFalse();
        pred(P("bad-name", name: "Gadget", price: 10m, categoryId: "c1", active: true)).Should().BeFalse();
        pred(P("bad-price", name: "Widget", price: 99m, categoryId: "c1", active: true)).Should().BeFalse();
        pred(P("bad-active", name: "Widget", price: 10m, categoryId: "c1", active: false)).Should().BeFalse();
    }

    // ---------- Create / Update / Delete ----------

    [Fact]
    public async Task Create_Maps_Adds_And_Returns_Dto()
    {
        var h = Build();
        Product? added = null;
        h.Products.Setup(r => r.AddAsync(It.IsAny<Product>()))
            .Callback((Product p) => added = p).Returns(Task.CompletedTask);

        var result = await h.Service.CreateProductAsync(new CreateProductDto
        {
            Name = "New", Description = "d", Price = 7m, CategoryId = "c1"
        });

        result.Name.Should().Be("New");
        added.Should().NotBeNull();
        added!.Name.Should().Be("New");
        h.Products.Verify(r => r.AddAsync(It.IsAny<Product>()), Times.Once);
    }

    [Fact]
    public async Task Update_Missing_Throws_And_Never_Invalidates()
    {
        var h = Build();

        var act = () => h.Service.UpdateProductAsync("ghost", new UpdateProductDto { Name = "x" });

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Product not found*");
        h.Cache.Verify(c => c.Remove(It.IsAny<string>()), Times.Never);
        h.Products.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<Product>()), Times.Never);
    }

    [Fact]
    public async Task Update_Success_Persists_And_Invalidates_Cache()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1") });

        var result = await h.Service.UpdateProductAsync("p1", new UpdateProductDto { Name = "Renamed" });

        result.Name.Should().Be("Renamed");
        h.Products.Verify(r => r.UpdateAsync("p1", It.IsAny<Product>()), Times.Once);
        h.Cache.Verify(c => c.Remove("product:p1"), Times.Once);
    }

    [Fact]
    public async Task Delete_Missing_Throws_And_Never_Invalidates()
    {
        var h = Build();

        var act = () => h.Service.DeleteProductAsync("ghost");

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Product not found*");
        h.Cache.Verify(c => c.Remove(It.IsAny<string>()), Times.Never);
        h.Products.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Delete_Success_Removes_And_Invalidates_Cache()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1") });

        await h.Service.DeleteProductAsync("p1");

        h.Products.Verify(r => r.DeleteAsync("p1"), Times.Once);
        h.Cache.Verify(c => c.Remove("product:p1"), Times.Once);
    }
}
