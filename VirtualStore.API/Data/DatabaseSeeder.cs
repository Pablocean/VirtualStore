using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.API.Data;

public class DatabaseSeeder
{
    private readonly IRepository<User> _userRepository;
    private readonly ILogger<DatabaseSeeder> _logger;
    private readonly IConfiguration _configuration;

    public DatabaseSeeder(
        IRepository<User> userRepository,
        ILogger<DatabaseSeeder> logger,
        IConfiguration configuration)
    {
        _userRepository = userRepository;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task SeedAsync()
    {
        var configuredEmail = _configuration["DatabaseSeeder:AdminEmail"];
        var configuredPassword = _configuration["DatabaseSeeder:AdminPassword"];
        var configuredUsername = _configuration["DatabaseSeeder:AdminUsername"];

        var usingDefaults = string.IsNullOrWhiteSpace(configuredEmail)
            || string.IsNullOrWhiteSpace(configuredPassword)
            || string.IsNullOrWhiteSpace(configuredUsername);

        var adminEmail = configuredEmail ?? "admin@virtualstore.com";
        var adminPassword = configuredPassword ?? "Admin123!";
        var adminUsername = configuredUsername ?? "admin";

        // Check if any admin already exists (by email or role)
        var existingAdmin = await _userRepository.FindOneAsync(u => u.Email == adminEmail);
        if (existingAdmin != null && existingAdmin.Roles.Contains(UserRole.Admin))
        {
            return;
        }

        var admin = new User
        {
            Email = adminEmail,
            Username = adminUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            FirstName = "System",
            LastName = "Admin",
            EmailConfirmed = true,
            TwoFactorEnabled = false,
            Roles = new List<UserRole> { UserRole.Admin, UserRole.Manager, UserRole.Customer }
        };

        await _userRepository.AddAsync(admin);

        if (usingDefaults)
            _logger.LogWarning("Default admin credentials are in use. Admin user created: {Email} / {Username}", adminEmail, adminUsername);
        else
            _logger.LogInformation("Admin user created: {Email} / {Username}", adminEmail, adminUsername);
    }
}
