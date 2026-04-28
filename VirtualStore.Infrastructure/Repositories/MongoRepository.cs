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

    public async Task<T?> GetByIdAsync(string id)
        => await _collection.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync();

    public async Task<IEnumerable<T>> GetAllAsync()
        => await _collection.Find(x => !x.IsDeleted).ToListAsync();

    public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
        => await _collection.Find(
            Builders<T>.Filter.And(
                Builders<T>.Filter.Eq(x => x.IsDeleted, false),
                Builders<T>.Filter.Where(predicate)
            )).ToListAsync();

    public async Task<T?> FindOneAsync(Expression<Func<T, bool>> predicate)
        => await _collection.Find(
            Builders<T>.Filter.And(
                Builders<T>.Filter.Eq(x => x.IsDeleted, false),
                Builders<T>.Filter.Where(predicate)
            )).FirstOrDefaultAsync();

    public async Task AddAsync(T entity)
    {
        entity.CreatedAt = DateTime.UtcNow;
        await _collection.InsertOneAsync(entity);
    }

    public async Task UpdateAsync(string id, T entity)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        await _collection.ReplaceOneAsync(x => x.Id == id, entity);
    }

    public async Task DeleteAsync(string id)
    {
        var filter = Builders<T>.Filter.Eq(x => x.Id, id);
        var update = Builders<T>.Update.Set(x => x.IsDeleted, true)
                                       .Set(x => x.UpdatedAt, DateTime.UtcNow);
        await _collection.UpdateOneAsync(filter, update);
    }

    public async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate)
        => await _collection.Find(
            Builders<T>.Filter.And(
                Builders<T>.Filter.Eq(x => x.IsDeleted, false),
                Builders<T>.Filter.Where(predicate)
            )).AnyAsync();
}