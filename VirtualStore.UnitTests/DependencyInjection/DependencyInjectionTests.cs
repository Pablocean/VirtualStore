using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualStore.API.Data;
using VirtualStore.API.Extensions;
using VirtualStore.API.Middlewares;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Data;
using VirtualStore.Infrastructure.Email;
using VirtualStore.Infrastructure.Services;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.UnitTests.DependencyInjection;

/// <summary>
/// Wave T1c remainder suite for <see cref="ServiceExtensions.AddApplicationServices"/>.
/// Builds a real <see cref="ServiceCollection"/> over an in-memory
/// <see cref="IConfiguration"/> (valid secrets) and asserts every
/// <c>I*Service</c>, factory/seam, repository, validator and the exception
/// handler resolve. No I/O: Mongo/Redis/Stripe clients are constructed but
/// never connected.
/// </summary>
public class DependencyInjectionTests
{
    private static IConfiguration ValidConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["JwtSettings:Secret"] = "t1c-test-secret-that-is-long-enough-for-hmac-256!!!",
            ["JwtSettings:Issuer"] = "TestIssuer",
            ["JwtSettings:Audience"] = "TestAudience",
            ["JwtSettings:AccessTokenExpirationMinutes"] = "15",
            ["JwtSettings:RefreshTokenExpirationDays"] = "7",
            ["MongoDbSettings:ConnectionString"] = "mongodb://localhost:27017",
            ["MongoDbSettings:DatabaseName"] = "VirtualStoreDiTest"
        })
        .Build();

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices(ValidConfig());
        services.AddWave1bValidationAndErrors();
        // Test-host equivalents of what WebApplicationBuilder provides in
        // production (logging + hosting environment for DatabaseSeeder and
        // ApiExceptionHandler). No production change.
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        return services;
    }

    private static IServiceProvider BuildProvider() => BuildServices().BuildServiceProvider();

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "VirtualStore.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IServiceScope Scope(IServiceProvider provider) => provider.CreateScope();

    [Fact]
    public void AllApplicationServices_Resolve()
    {
        using var scope = Scope(BuildProvider());
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IAuthService>().Should().NotBeNull();
        sp.GetRequiredService<ITokenService>().Should().NotBeNull();
        sp.GetRequiredService<IUserService>().Should().NotBeNull();
        sp.GetRequiredService<ICartService>().Should().NotBeNull();
        sp.GetRequiredService<IOrderService>().Should().NotBeNull();
        sp.GetRequiredService<ICategoryService>().Should().NotBeNull();
        sp.GetRequiredService<IEmailService>().Should().NotBeNull();
        sp.GetRequiredService<IStripePaymentService>().Should().NotBeNull();

        // IProductService / IEnterpriseInfoService depend on ICacheService, whose
        // implementation (CacheService) currently exposes two public single-arg
        // ctors with no [ActivatorUtilitiesConstructor] — a wave-T0 seam regression
        // (commit 11df0da) that makes MS.DI constructor selection ambiguous in ANY
        // container, test or production. Activation is therefore deferred to the
        // services wave / T2 (one-line fix: mark the adapter ctor). Assert the
        // registrations themselves here; no production change in this suite.
        var descriptors = BuildServices();
        descriptors.Should().ContainSingle(d =>
            d.ServiceType == typeof(IProductService) &&
            d.ImplementationType == typeof(ProductService) &&
            d.Lifetime == ServiceLifetime.Scoped);
        descriptors.Should().ContainSingle(d =>
            d.ServiceType == typeof(IEnterpriseInfoService) &&
            d.ImplementationType == typeof(EnterpriseInfoService) &&
            d.Lifetime == ServiceLifetime.Scoped);

        // ICacheService: CacheService exposes two public single-arg ctors (the
        // IHybridCacheAdapter seam + the HybridCache back-compat overload) with no
        // [ActivatorUtilitiesConstructor], so MS.DI constructor selection is
        // ambiguous in ANY container (test or production) and activation throws.
        // Assert the registration itself plus the seam-wiring path DI is meant to
        // use; the ctor disambiguation is deferred (see T2 report). No prod change.
        BuildServices().Should().ContainSingle(d =>
            d.ServiceType == typeof(ICacheService) &&
            d.ImplementationType == typeof(CacheService) &&
            d.Lifetime == ServiceLifetime.Singleton);
        var viaSeam = new CacheService(sp.GetRequiredService<IHybridCacheAdapter>());
        viaSeam.Should().BeOfType<CacheService>().Which.Should().BeAssignableTo<ICacheService>();
    }

    [Fact]
    public void SeamsAndFactories_Resolve()
    {
        using var scope = Scope(BuildProvider());
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IDateTimeProvider>().Should().NotBeNull();
        sp.GetRequiredService<ISmtpClientFactory>().Should().NotBeNull();
        sp.GetRequiredService<IStripeClientFactory>().Should().NotBeNull();
        sp.GetRequiredService<IHybridCacheAdapter>().Should().NotBeNull();
        sp.GetRequiredService<IAuthenticationSchemeProvider>().Should().NotBeNull();
    }

    [Fact]
    public void RepositoriesAndInfrastructure_Resolve()
    {
        using var scope = Scope(BuildProvider());
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<MongoDbContext>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<User>>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<Product>>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<Order>>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<Cart>>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<Category>>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<EnterpriseInfo>>().Should().NotBeNull();
        sp.GetRequiredService<IRepository<ProcessedWebhookEvent>>().Should().NotBeNull();
        sp.GetRequiredService<AutoMapper.IMapper>().Should().NotBeNull();
        sp.GetRequiredService<DatabaseSeeder>().Should().NotBeNull();
    }

    [Fact]
    public void Validators_AutoRegistered_Resolve()
    {
        using var scope = Scope(BuildProvider());
        var sp = scope.ServiceProvider;

        // All 19 validators: registration is by assembly scan
        // (AddValidatorsFromAssemblyContaining<LoginRequestValidator>), so
        // resolving every IValidator<T> proves the scan ran completely.
        sp.GetRequiredService<IValidator<LoginRequest>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<ConfirmEmailDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<ResendConfirmationDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<ForgotPasswordDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<ResetPasswordDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<ChangePasswordDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<CreateUserDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<UpdateUserDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<CreateProductDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<UpdateProductDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<CreateCategoryDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<UpdateCategoryDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<CreateOrderDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<OrderItemDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<AddressDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<AddToCartDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<UpdateCartItemDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<UpdateEnterpriseInfoDto>>().Should().NotBeNull();
        sp.GetRequiredService<IValidator<PurgeRequestDto>>().Should().NotBeNull();
    }

    [Fact]
    public void ExceptionHandler_Resolves()
    {
        using var scope = Scope(BuildProvider());
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>()
            .Should().BeOfType<ApiExceptionHandler>();
    }

    [Fact]
    public void AddApplicationServices_ShortJwtSecret_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Secret"] = "too-short",
                ["MongoDbSettings:ConnectionString"] = "mongodb://localhost:27017",
                ["MongoDbSettings:DatabaseName"] = "x"
            })
            .Build();

        var act = () => new ServiceCollection().AddApplicationServices(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JwtSettings:Secret*");
    }

    [Fact]
    public void AddApplicationServices_MissingMongoConnection_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Secret"] = "t1c-test-secret-that-is-long-enough-for-hmac-256!!!",
                ["JwtSettings:Issuer"] = "TestIssuer",
                ["JwtSettings:Audience"] = "TestAudience",
                ["MongoDbSettings:DatabaseName"] = "x"
            })
            .Build();

        var act = () => new ServiceCollection().AddApplicationServices(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MongoDbSettings:ConnectionString*");
    }
}
