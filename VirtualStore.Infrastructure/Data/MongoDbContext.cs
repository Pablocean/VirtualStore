using Microsoft.Extensions.Options;
using MongoDB.Driver;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Data;

public class MongoDbContext
{
    public IMongoDatabase Database { get; }
    
    public MongoDbContext(IOptions<MongoDbSettings> settings)
    {
        var client = new MongoClient(settings.Value.ConnectionString);
        Database = client.GetDatabase(settings.Value.DatabaseName);
    }
    
    public IMongoCollection<T> GetCollection<T>(string name)
        => Database.GetCollection<T>(name);

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

        // ParentCategoryId on Category.
        var categoryCollection = GetCollection<Category>(typeof(Category).Name);
        var categoryParentIndex = new CreateIndexModel<Category>(
            Builders<Category>.IndexKeys.Ascending(c => c.ParentCategoryId),
            new CreateIndexOptions { Name = "ix_category_parentCategoryId" });
        await categoryCollection.Indexes.CreateOneAsync(categoryParentIndex, cancellationToken: ct);
    }
}
