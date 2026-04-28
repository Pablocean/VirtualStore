using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Quartz;
using Serilog;
using Serilog.Extensions.Hosting;
using System.Text;
using VirtualStore.API.Data;
using VirtualStore.Application.Interfaces;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.BackgroundServices;
using VirtualStore.Infrastructure.Data;
using VirtualStore.Infrastructure.Email;
using VirtualStore.Infrastructure.Repositories;
using VirtualStore.Infrastructure.Services;
using VirtualStore.Infrastructure.Stripe;

namespace VirtualStore.API.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration config)
    {
        // Settings
        services.Configure<MongoDbSettings>(config.GetSection("MongoDbSettings"));
        services.Configure<JwtSettings>(config.GetSection("JwtSettings"));
        services.Configure<EmailSettings>(config.GetSection("EmailSettings"));
        services.Configure<StripeSettings>(config.GetSection("StripeSettings"));

        // MongoDB Context & generic repository
        services.AddSingleton<MongoDbContext>();
        services.AddScoped(typeof(IRepository<>), typeof(MongoRepository<>));

        // Register all services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICartService, CartService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IEnterpriseInfoService, EnterpriseInfoService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IStripePaymentService, StripePaymentService>();
        services.AddSingleton<ICacheService, CacheService>();

        // AutoMapper
        var mapperConfig = new AutoMapper.MapperConfiguration(
            cfg => cfg.AddProfile<MappingProfile>(),
            NullLoggerFactory.Instance
        );
        mapperConfig.AssertConfigurationIsValid();
        services.AddSingleton<IMapper>(mapperConfig.CreateMapper());

        // Caching
        services.AddMemoryCache();

        //Seeder
        services.AddScoped<DatabaseSeeder>();

        // Quartz for background tasks
        services.AddQuartz(q =>
        {
            var jobKey = new JobKey("RefreshTokenCleanupJob");
            q.AddJob<RefreshTokenCleanupJob>(opts => opts.WithIdentity(jobKey));
            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("RefreshTokenCleanup-trigger")
                .WithCronSchedule("0 0 3 * * ?") // Daily at 3:00 AM
            );
        });
        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        // JWT Authentication
        var jwtSettings = config.GetSection("JwtSettings").Get<JwtSettings>()!;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtSettings.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

        // CORS
        services.AddCors(options =>
        {
            options.AddPolicy("CorsPolicy", builder =>
                builder.WithOrigins(config.GetSection("CorsSettings:AllowedOrigins").Get<string[]>()!)
                       .AllowAnyMethod()
                       .AllowAnyHeader()
                       .AllowCredentials());
        });

        var mongoSettings = config.GetSection("MongoDbSettings").Get<MongoDbSettings>()!;

        services.AddHealthChecks()
    .AddMongoDb(
        sp => new MongoClient(mongoSettings.ConnectionString),
        name: "mongodb",
        tags: ["ready"]);

        return services;
    }

    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddOpenApi(); // Native OpenAPI in .NET 10
        // For Swagger UI you can use Scalar: app.MapScalarApiReference();
        return services;
    }
}