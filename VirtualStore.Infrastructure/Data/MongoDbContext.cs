using Microsoft.Extensions.Options;
using MongoDB.Driver;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Data;

public class MongoDbContext
{
    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    private readonly AsyncLocal<IClientSessionHandle?> _currentSession = new();

    /// <summary>
    /// Ambient session set by <see cref="TransactAsync"/> for the current async flow.
    /// <see cref="Repositories.MongoRepository{T}"/> enlists in it automatically.
    /// Null outside a transaction (including all unit tests with mocked repos).
    /// </summary>
    public IClientSessionHandle? CurrentSession
    {
        get => _currentSession.Value;
        private set => _currentSession.Value = value;
    }
    
    public MongoDbContext(IOptions<MongoDbSettings> settings)
    {
        Client = new MongoClient(settings.Value.ConnectionString);
        Database = Client.GetDatabase(settings.Value.DatabaseName);
    }
    
    public IMongoCollection<T> GetCollection<T>(string name)
        => Database.GetCollection<T>(name);

    /// <summary>
    /// Runs <paramref name="work"/> inside a multi-document transaction on a fresh
    /// session, exposing it as <see cref="CurrentSession"/> for the duration.
    /// Requires a replica set (compose/CI/Testcontainers use mongodb-community-server).
    /// Re-entrant: when already inside a transaction the work runs on the ambient
    /// session without opening a nested transaction. Driver-level retry inside
    /// <c>WithTransactionAsync</c> may re-run <paramref name="work"/> on transient
    /// errors, so transactional work must tolerate re-execution.
    /// Transaction aborts surface as <see cref="MongoException"/> (mapped to 500
    /// by the API handler; see ADR-0006 follow-ups).
    /// </summary>
    public async Task TransactAsync(Func<Task> work, CancellationToken ct)
    {
        if (CurrentSession is not null)
        {
            await work().ConfigureAwait(false);
            return;
        }

        using var session = await Client.StartSessionAsync(cancellationToken: ct).ConfigureAwait(false);
        await session.WithTransactionAsync(async (s, _) =>
        {
            CurrentSession = s;
            try
            {
                await work().ConfigureAwait(false);
            }
            finally
            {
                CurrentSession = null;
            }
            return true;
        }, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures MongoDB indexes. Collection names follow typeof(T).Name (no renames).
    /// Non-fatal: callers should wrap in try/catch and log a warning.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // Unique Email on User.
        var userCollection = GetCollection<User>(typeof(User).Name);
        var emailIndex = new CreateIndexModel<User>(
            Builders<User>.IndexKeys.Ascending(u => u.Email),
            new CreateIndexOptions { Unique = true, Name = "ux_user_email" });
        await userCollection.Indexes.CreateOneAsync(emailIndex, cancellationToken: ct);

        // CategoryId on Product.
        var productCollection = GetCollection<Product>(typeof(Product).Name);
        var productCategoryIndex = new CreateIndexModel<Product>(
            Builders<Product>.IndexKeys.Ascending(p => p.CategoryId),
            new CreateIndexOptions { Name = "ix_product_categoryId" });
        await productCollection.Indexes.CreateOneAsync(productCategoryIndex, cancellationToken: ct);

        // UserId unique on Cart.
        var cartCollection = GetCollection<Cart>(typeof(Cart).Name);
        var cartUserIndex = new CreateIndexModel<Cart>(
            Builders<Cart>.IndexKeys.Ascending(c => c.UserId),
            new CreateIndexOptions { Unique = true, Name = "ux_cart_userId" });
        await cartCollection.Indexes.CreateOneAsync(cartUserIndex, cancellationToken: ct);

        // UserId on Order.
        var orderCollection = GetCollection<Order>(typeof(Order).Name);
        var orderUserIndex = new CreateIndexModel<Order>(
            Builders<Order>.IndexKeys.Ascending(o => o.UserId),
            new CreateIndexOptions { Name = "ix_order_userId" });
        await orderCollection.Indexes.CreateOneAsync(orderUserIndex, cancellationToken: ct);

        // Idempotency replay guard: one order per (UserId, IdempotencyKey).
        // Sparse so orders without a key (field omitted via BsonIgnoreIfNull) are
        // not indexed and never collide. See ADR-0006.
        var orderIdempotencyIndex = new CreateIndexModel<Order>(
            Builders<Order>.IndexKeys.Ascending(o => o.UserId).Ascending(o => o.IdempotencyKey),
            new CreateIndexOptions { Unique = true, Sparse = true, Name = "ux_order_userIdempotency" });
        await orderCollection.Indexes.CreateOneAsync(orderIdempotencyIndex, cancellationToken: ct);

        // ParentCategoryId on Category.
        var categoryCollection = GetCollection<Category>(typeof(Category).Name);
        var categoryParentIndex = new CreateIndexModel<Category>(
            Builders<Category>.IndexKeys.Ascending(c => c.ParentCategoryId),
            new CreateIndexOptions { Name = "ix_category_parentCategoryId" });
        await categoryCollection.Indexes.CreateOneAsync(categoryParentIndex, cancellationToken: ct);

        // Webhook dedup (ADR-0007): one record per Stripe event id + 30-day TTL.
        var webhookCollection = GetCollection<ProcessedWebhookEvent>(typeof(ProcessedWebhookEvent).Name);
        var webhookEventIndex = new CreateIndexModel<ProcessedWebhookEvent>(
            Builders<ProcessedWebhookEvent>.IndexKeys.Ascending(e => e.EventId),
            new CreateIndexOptions { Unique = true, Name = "ux_webhookevent_eventId" });
        await webhookCollection.Indexes.CreateOneAsync(webhookEventIndex, cancellationToken: ct);
        var webhookTtlIndex = new CreateIndexModel<ProcessedWebhookEvent>(
            Builders<ProcessedWebhookEvent>.IndexKeys.Ascending(e => e.ReceivedAt),
            new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30), Name = "ttl_webhookevent_receivedAt" });
        await webhookCollection.Indexes.CreateOneAsync(webhookTtlIndex, cancellationToken: ct);
    }
}
