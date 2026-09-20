using MongoDB.Driver;
using System.Linq.Expressions;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Data;

namespace VirtualStore.Infrastructure.Repositories;

public class MongoRepository<T> : IRepository<T> where T : BaseEntity
{
    protected readonly IMongoCollection<T> _collection;

    public MongoRepository(MongoDbContext context)
    {
        // Collection name = entity type name (e.g. "User", "Product")
        _collection = context.GetCollection<T>(typeof(T).Name);
    }

    private static FilterDefinition<T> BuildFilter(Expression<Func<T, bool>> predicate)
    {
        // Soft-delete aware: exclude IsDeleted unless the predicate itself references it.
        var referencesIsDeleted = predicate.ToString().Contains("IsDeleted", StringComparison.Ordinal);
        if (referencesIsDeleted)
            return Builders<T>.Filter.Where(predicate);

        return Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(x => x.IsDeleted, false),
            Builders<T>.Filter.Where(predicate));
    }

    private static FilterDefinition<T> NotDeletedFilter()
        => Builders<T>.Filter.Eq(x => x.IsDeleted, false);

    // ---- Existing signatures (delegate to CancellationToken overloads) ----

    public Task<T?> GetByIdAsync(string id)
        => GetByIdAsync(id, CancellationToken.None);

    public Task<IEnumerable<T>> GetAllAsync()
        => GetAllAsync(CancellationToken.None);

    public Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
        => FindAsync(predicate, CancellationToken.None);

    public Task<T?> FindOneAsync(Expression<Func<T, bool>> predicate)
        => FindOneAsync(predicate, CancellationToken.None);

    public Task AddAsync(T entity)
        => AddAsync(entity, CancellationToken.None);

    public Task UpdateAsync(string id, T entity)
        => UpdateAsync(id, entity, CancellationToken.None);

    public Task DeleteAsync(string id)
        => DeleteAsync(id, CancellationToken.None);

    public Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate)
        => ExistsAsync(predicate, CancellationToken.None);

    // ---- CancellationToken overloads ----

    public async Task<T?> GetByIdAsync(string id, CancellationToken ct)
        => await _collection.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(ct);

    public async Task<IEnumerable<T>> GetAllAsync(CancellationToken ct)
        => await _collection.Find(x => !x.IsDeleted).ToListAsync(ct);

    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
        => await _collection.Find(BuildFilter(predicate)).ToListAsync(ct);

    public async Task<T?> FindOneAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
        => await _collection.Find(BuildFilter(predicate)).FirstOrDefaultAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct)
    {
        entity.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(entity, cancellationToken: ct);
    }

    public async Task UpdateAsync(string id, T entity, CancellationToken ct)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        await _collection.ReplaceOneAsync(x => x.Id == id, entity, cancellationToken: ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var filter = Builders<T>.Filter.Eq(x => x.Id, id);
        var update = Builders<T>.Update.Set(x => x.IsDeleted, true)
                                       .Set(x => x.UpdatedAt, DateTime.UtcNow);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
        => await _collection.Find(BuildFilter(predicate)).AnyAsync(ct);

    // ---- Paging / counting (server-side) ----

    public async Task<long> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await _collection.CountDocumentsAsync(BuildFilter(predicate), cancellationToken: ct);

    public async Task<(IReadOnlyList<T> Items, long Total)> PagedAsync(
        Expression<Func<T, bool>> predicate,
        int page,
        int size,
        string? sortBy,
        bool desc,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 20;
        if (size > 100) size = 100;

        var filter = BuildFilter(predicate);
        var total = await _collection.CountDocumentsAsync(filter, cancellationToken: ct);

        var find = _collection.Find(filter);

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            try
            {
                find = desc
                    ? find.Sort(Builders<T>.Sort.Descending(sortBy))
                    : find.Sort(Builders<T>.Sort.Ascending(sortBy));
            }
            catch (ArgumentException)
            {
                // Unknown sort field: fall back to CreatedAt desc for determinism.
                find = find.Sort(Builders<T>.Sort.Descending(x => x.CreatedAt));
            }
        }
        else
        {
            find = find.Sort(Builders<T>.Sort.Descending(x => x.CreatedAt));
        }

        var items = await find.Skip((page - 1) * size).Limit(size).ToListAsync(ct);
        return (items, total);
    }
}
