using Testcontainers.MongoDb;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// Starts a MongoDB Testcontainer (mongo:7.0) when Docker is available.
/// When Docker is unavailable, initialization swallows the failure and
/// <see cref="IsAvailable"/> stays false so tests SKIP gracefully.
/// </summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    private MongoDbContainer? _container;

    public bool IsAvailable => _container is not null;

    public string? ConnectionString => _container?.GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MongoDbBuilder()
                .WithImage("mongo:7.0")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker unavailable (no daemon, no socket, CI without docker).
            // Leave _container null so tests skip instead of failing.
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* ignore */ }
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            try { await _container.DisposeAsync(); } catch { /* ignore on teardown */ }
            _container = null;
        }
    }
}
