using AutoMapper;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Quartz;
using Serilog;
using Serilog.Extensions.Hosting;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using VirtualStore.API.Data;
using VirtualStore.API.Middlewares;
using VirtualStore.Application.Common;
using VirtualStore.Application.Interfaces;
using VirtualStore.Application.Mappings;
using VirtualStore.Application.Validators;
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
        ValidateRequiredConfiguration(config);

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

        // ===== BEGIN wave/t0-seams additions (injectable testability seams; parallel waves edit this file too) =====
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<ISmtpClientFactory, SmtpClientFactory>();
        services.AddSingleton<IStripeClientFactory, StripeClientFactory>();
        services.AddSingleton<IHybridCacheAdapter, HybridCacheAdapter>();
        // ===== END wave/t0-seams additions =====

        // ===== BEGIN wave/1d-orders-payments additions (keep grouped; parallel waves edit this file too) =====
        services.AddScoped<ICategoryService, CategoryService>();
        // ===== END wave/1d-orders-payments additions =====

        // AutoMapper
        var mapperConfig = new AutoMapper.MapperConfiguration(
            cfg => cfg.AddProfile<MappingProfile>(),
            NullLoggerFactory.Instance
        );
        mapperConfig.AssertConfigurationIsValid();
        services.AddSingleton<IMapper>(mapperConfig.CreateMapper());

        // Caching
        services.AddMemoryCache();
        AddWave3aFHybridCache(services, config);

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
                // Missed 03:00 firings are skipped; the next night catches up
                // (job is idempotent — see RefreshTokenCleanupJob, ADR-0010).
                .WithCronSchedule("0 0 3 * * ?", x => x.WithMisfireHandlingInstructionDoNothing())
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

        // ===== BEGIN wave/2f-hardening wiring (implementations live in the Wave 2f region below) =====
        AddWave2fCors(services, config);
        AddWave2fRateLimiting(services);
        AddWave2fOpenTelemetry(services, config);
        // ===== END wave/2f-hardening wiring =====

        var mongoSettings = config.GetSection("MongoDbSettings").Get<MongoDbSettings>()!;

        services.AddHealthChecks()
    .AddMongoDb(
        sp => new MongoClient(mongoSettings.ConnectionString),
        name: "mongodb",
        tags: ["ready"]);

        return services;
    }

    #region Wave 3a-D - Startup config validation
    // Fail-fast single-source-of-truth guard (Epic D). Called at the TOP of
    // AddApplicationServices, before JWT Auth setup. Uses IConfiguration reads
    // (IOptions isn't built yet at this point — read via config.GetSection().Get<T>()).
    public static void ValidateRequiredConfiguration(IConfiguration config)
    {
        var jwt = config.GetSection("JwtSettings").Get<JwtSettings>();
        if (string.IsNullOrEmpty(jwt?.Secret) || jwt.Secret.Length < 32)
            throw new InvalidOperationException(
                "Startup config validation failed: JwtSettings:Secret (env JwtSettings__Secret) " +
                "must be at least 32 characters. Fix: set a 64-char random secret via " +
                "JwtSettings__Secret in .env / environment.");

        var mongo = config.GetSection("MongoDbSettings").Get<MongoDbSettings>();
        if (string.IsNullOrWhiteSpace(mongo?.ConnectionString))
            throw new InvalidOperationException(
                "Startup config validation failed: MongoDbSettings:ConnectionString " +
                "(env MongoDbSettings__ConnectionString) must be non-empty. Fix: set " +
                "MongoDbSettings__ConnectionString in .env / environment " +
                "(e.g. mongodb://localhost:27017).");

        var envName = config["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var isProduction = string.Equals(envName, "Production", StringComparison.OrdinalIgnoreCase);

        var stripe = config.GetSection("StripeSettings").Get<StripeSettings>();
        var email = config.GetSection("EmailSettings").Get<EmailSettings>();

        if (isProduction)
        {
            if (string.IsNullOrWhiteSpace(stripe?.SecretKey))
                throw new InvalidOperationException(
                    "Startup config validation failed: StripeSettings:SecretKey " +
                    "(env StripeSettings__SecretKey) is required in Production. Fix: set " +
                    "StripeSettings__SecretKey in Production environment / vault.");
            if (string.IsNullOrWhiteSpace(email?.Password))
                throw new InvalidOperationException(
                    "Startup config validation failed: EmailSettings:Password " +
                    "(env EmailSettings__Password) is required in Production. Fix: set " +
                    "EmailSettings__Password in Production environment / vault.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(stripe?.SecretKey))
                Console.WriteLine("WARNING: StripeSettings:SecretKey (env StripeSettings__SecretKey) is empty — payments will fail until configured.");
            if (string.IsNullOrWhiteSpace(email?.Password))
                Console.WriteLine("WARNING: EmailSettings:Password (env EmailSettings__Password) is empty — OTP email will fail until configured.");
        }
    }
    #endregion

    #region Wave 3a-F - HybridCache + distributed OTP storage (Epic F-lite, ADR-0011)
    // HybridCache (memory L1 by default) + optional Redis L2. IDistributedCache always
    // resolves: in-memory when Redis:ConnectionString is empty (single-instance default),
    // StackExchangeRedis when set (multi-instance OTP + shared L2). Called from
    // AddApplicationServices above; keep all wave/3a-hybridcache2 additions in this region.
    private static void AddWave3aFHybridCache(IServiceCollection services, IConfiguration config)
    {
        services.AddDistributedMemoryCache();

        var redisConnectionString = config["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            // Last IDistributedCache registration wins: Redis becomes both the
            // distributed OTP store and the HybridCache L2.
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
            });
        }

        services.AddHybridCache();
    }
    #endregion

    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddOpenApi(); // Native OpenAPI in .NET 10
        // For Swagger UI you can use Scalar: app.MapScalarApiReference();
        return services;
    }

    #region Wave 2f - Hardening (ops: CORS guard, rate limiting, OpenTelemetry)
    // All wave/2f-hardening service registrations live here. Called from AddApplicationServices above.

    private static void AddWave2fCors(IServiceCollection services, IConfiguration config)
    {
        // Null-safe origins read with localhost fallback.
        var allowedOrigins = config.GetSection("CorsSettings:AllowedOrigins").Get<string[]>()
            ?.Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .ToArray();
        if (allowedOrigins is null || allowedOrigins.Length == 0)
            allowedOrigins = ["http://localhost:3000"];

        // Fail fast: AllowAnyOrigin + AllowCredentials is rejected by the CORS middleware
        // at runtime — surface the misconfiguration at startup instead.
        if (allowedOrigins.Any(o => o == "*"))
            throw new InvalidOperationException(
                "CORS misconfiguration: wildcard origin '*' cannot be combined with AllowCredentials. " +
                "Configure explicit origins in CorsSettings:AllowedOrigins.");

        // AllowCredentials is only used with the explicit origin list above (never with AllowAnyOrigin).
        services.AddCors(options =>
        {
            options.AddPolicy("CorsPolicy", builder =>
                builder.WithOrigins(allowedOrigins)
                       .AllowAnyMethod()
                       .AllowAnyHeader()
                       .AllowCredentials());
        });
    }

    private static void AddWave2fRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // RateLimiter short-circuits before the ApiExceptionHandler pipeline, so emit
            // a ProblemDetails body here to keep the 429 shape consistent.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too Many Requests",
                    Detail = "Rate limit exceeded. Please retry after a short delay.",
                    Instance = context.HttpContext.Request.Path
                };
                await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
            };

            static RateLimitPartition<string> PerIpSlidingWindow(HttpContext httpContext, int permitLimit) =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 1,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });

            // "auth": strict per-IP budget for AuthController
            // (applied via [EnableRateLimiting("auth")] on that controller).
            options.AddPolicy("auth", httpContext => PerIpSlidingWindow(httpContext, permitLimit: 5));

            // "webhook": per-IP budget for StripeWebhookController (ADR-0007).
            // Stripe bursts redeliveries on failures; 60/min absorbs retry storms
            // without throttling legitimate traffic. AuthController stays on "auth".
            options.AddPolicy("webhook", httpContext => PerIpSlidingWindow(httpContext, permitLimit: 60));

            // Global fallback: sliding 100 req/min per IP for everything else.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                httpContext => PerIpSlidingWindow(httpContext, permitLimit: 100));
        });
    }

    private static void AddWave2fOpenTelemetry(IServiceCollection services, IConfiguration config)
    {
        var otlpEndpoint = config["Otlp:Endpoint"];
        var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(otlpEndpoint);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: "VirtualStore.API"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                // OTLP export is opt-in: only enabled when Otlp:Endpoint is configured.
                if (hasOtlpEndpoint)
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation();
                if (hasOtlpEndpoint)
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
            });
    }
    #endregion

    /// <summary>
    /// Dev-only: wires the JWT Bearer security scheme into the OpenAPI document
    /// so Scalar shows the Authorize button. Only call in Development
    /// (the OpenAPI/Scalar endpoints are mapped dev-only in Program.cs).
    /// </summary>
    public static IServiceCollection AddDevOpenApiJwtSecurity(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<OpenApi.BearerSecuritySchemeTransformer>();
        });
        return services;
    }

    #region Wave 1b - Validation + ProblemDetails (parallel-wave merge zone: keep wave-1b additions inside this region)
    public static IServiceCollection AddWave1bValidationAndErrors(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();

        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();
        services.AddFluentValidationAutoValidation();

        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        return services;
    }
    #endregion
}