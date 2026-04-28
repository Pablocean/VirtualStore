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
        var adminEmail = _configuration["DatabaseSeeder:AdminEmail"] ?? "admin@virtualstore.com";
        var adminPassword = _configuration["DatabaseSeeder:AdminPassword"] ?? "Admin123!";
        var adminUsername = _configuration["DatabaseSeeder:AdminUsername"] ?? "admin";

        // Check if any admin already exists (by email or role)
        var existingAdmin = await _userRepository.FindOneAsync(u => u.Email == adminEmail);
        if (existingAdmin != null && existingAdmin.Roles.Contains(UserRole.Admin))
        {
            _logger.LogInformation("Admin user ({Email}) already exists. Skipping seed.", adminEmail);
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
        _logger.LogInformation("Default admin user created: {Email} / {Password}", adminEmail, adminPassword);
    }
}