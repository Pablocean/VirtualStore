using dotenv.net;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using Serilog;
using System.Diagnostics.CodeAnalysis;
using VirtualStore.API.Extensions;

try
{
    DotEnv.Load(options: new DotEnvOptions(probeForEnv: true, probeLevelsToSearch: 5));
}
catch (Exception ex)
{
    Console.WriteLine("Failed to load .env: " + ex.Message);
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Basic logging before host build
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .CreateLogger();
    builder.Host.UseSerilog();

    builder.Services.AddApplicationServices(builder.Configuration);
    builder.Services.AddSwaggerDocumentation();
    if (builder.Environment.IsDevelopment())
        builder.Services.AddDevOpenApiJwtSecurity(); // Scalar Authorize button (dev-only)
    builder.Services.AddControllers();
    builder.Services.AddWave1bValidationAndErrors();

    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    // Structured request logging (Serilog) with TraceIdentifier correlation.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms {TraceIdentifier}";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            diagnosticContext.Set("TraceIdentifier", httpContext.TraceIdentifier);
    });

    app.UseHttpsRedirection();
    app.UseCors("CorsPolicy");
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHealthChecks("/health").DisableRateLimiting();
    // Liveness probe: no checks, always Healthy ("self"). /health stays the readiness endpoint.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
    app.MapControllers();

    // Ensure MongoDB indexes (non-fatal)
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var mongoContext = scope.ServiceProvider.GetRequiredService<VirtualStore.Infrastructure.Data.MongoDbContext>();
            await mongoContext.EnsureIndexesAsync();
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(ex, "MongoDB index creation failed (non-fatal)");
        }
    }

    // Seed the database
    using (var scope = app.Services.CreateScope())
    {
        var seeder = scope.ServiceProvider.GetRequiredService<VirtualStore.API.Data.DatabaseSeeder>();
        await seeder.SeedAsync();
    }

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine("STARTUP ERROR: " + ex.GetType().Name + ": " + ex.Message);
    if (ex is AutoMapper.AutoMapperConfigurationException mapperEx)
    {
        Console.WriteLine("AutoMapper Errors:");
        foreach (var error in mapperEx.Errors)
            Console.WriteLine("  - " + error);
    }
    if (ex.InnerException != null)
        Console.WriteLine("INNER: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
    try { Console.WriteLine(ex.ToString()); } catch { Console.WriteLine("(Full exception string could not be printed)"); }
}

// Test entry point: enables WebApplicationFactory<Program> in VirtualStore.IntegrationTests.
// Wave T0: excluded from code coverage (host bootstrapping only).
[ExcludeFromCodeCoverage]
public partial class Program
{
}