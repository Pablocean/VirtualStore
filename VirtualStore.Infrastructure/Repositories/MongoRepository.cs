using MongoDB.Driver;
using System.Linq.Expressions;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Data;

namespace VirtualStore.Infrastructure.Repositories;

public class MongoRepository<T> : IRepository<T> where T : BaseEntity
{
    protected readonly IMongoCollection<T> _collection;
    private readonly MongoDbContext _context;

    public MongoRepository(MongoDbContext context)
    {
        _context = context;
        // Collection name = entity type name (e.g. "User", "Product")
        _collection = context.GetCollection<T>(typeof(T).Name);
    }

    /// <summary>
    /// Ambient transaction session from <see cref="MongoDbContext.TransactAsync"/>,
    /// or null outside a transaction (all existing behavior unchanged).
    /// </summary>
    private IClientSessionHandle? Session => _context.CurrentSession;

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
    {
        var filter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(x => x.Id, id),
            Builders<T>.Filter.Eq(x => x.IsDeleted, false));
        return Session is null
            ? await _collection.Find(filter).FirstOrDefaultAsync(ct)
            : await _collection.Find(Session, filter).FirstOrDefaultAsync(ct);
    }

    public async Task<IEnumerable<T>> GetAllAsync(CancellationToken ct)
    {
        var filter = NotDeletedFilter();
        return Session is null
            ? await _collection.Find(filter).ToListAsync(ct)
            : await _collection.Find(Session, filter).ToListAsync(ct);
    }

    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
    {
        var filter = BuildFilter(predicate);
        return Session is null
            ? await _collection.Find(filter).ToListAsync(ct)
            : await _collection.Find(Session, filter).ToListAsync(ct);
    }

    public async Task<T?> FindOneAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
    {
        var filter = BuildFilter(predicate);
        return Session is null
            ? await _collection.Find(filter).FirstOrDefaultAsync(ct)
            : await _collection.Find(Session, filter).FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(T entity, CancellationToken ct)
    {
        entity.CreatedAt = DateTime.UtcNow;
        if (Session is null)
            await _collection.InsertOneAsync(entity, cancellationToken: ct);
        else
            await _collection.InsertOneAsync(Session, entity, cancellationToken: ct);
    }

    public async Task UpdateAsync(string id, T entity, CancellationToken ct)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        var filter = Builders<T>.Filter.Eq(x => x.Id, id);
        if (Session is null)
            await _collection.ReplaceOneAsync(filter, entity, cancellationToken: ct);
        else
            await _collection.ReplaceOneAsync(Session, filter, entity, cancellationToken: ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var filter = Builders<T>.Filter.Eq(x => x.Id, id);
        var update = Builders<T>.Update.Set(x => x.IsDeleted, true)
                                       .Set(x => x.UpdatedAt, DateTime.UtcNow);
        if (Session is null)
            await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        else
            await _collection.UpdateOneAsync(Session, filter, update, cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct)
    {
        var filter = BuildFilter(predicate);
        return Session is null
            ? await _collection.Find(filter).AnyAsync(ct)
            : await _collection.Find(Session, filter).AnyAsync(ct);
    }

    // ---- Paging / counting (server-side) ----

    public async Task<long> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var filter = BuildFilter(predicate);
        return Session is null
            ? await _collection.CountDocumentsAsync(filter, cancellationToken: ct)
            : await _collection.CountDocumentsAsync(Session, filter, cancellationToken: ct);
    }

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
        var total = Session is null
            ? await _collection.CountDocumentsAsync(filter, cancellationToken: ct)
            : await _collection.CountDocumentsAsync(Session, filter, cancellationToken: ct);

        var find = Session is null ? _collection.Find(filter) : _collection.Find(Session, filter);

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
