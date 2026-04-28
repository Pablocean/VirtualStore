using dotenv.net;
using Scalar.AspNetCore;
using Serilog;
using VirtualStore.API.Extensions;
using VirtualStore.API.Middlewares;

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
        .CreateLogger();
    builder.Host.UseSerilog();

    builder.Services.AddApplicationServices(builder.Configuration);
    builder.Services.AddSwaggerDocumentation();
    builder.Services.AddControllers();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
        app.UseDeveloperExceptionPage();
    }

    // Inline request logging instead of UseSerilogRequestLogging()
    app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("HTTP {Method} {Path}", context.Request.Method, context.Request.Path);
        await next();
    });

    app.UseHttpsRedirection();
    app.UseCors("CorsPolicy");
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.MapHealthChecks("/health");
    app.MapControllers();

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