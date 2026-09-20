using System.Linq.Expressions;
using VirtualStore.Domain.Entities;

namespace VirtualStore.Domain.Interfaces;

public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(string id);
    Task<T?> GetByIdAsync(string id, CancellationToken ct);
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> GetAllAsync(CancellationToken ct);
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate);
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct);
    Task<T?> FindOneAsync(Expression<Func<T, bool>> predicate);
    Task<T?> FindOneAsync(Expression<Func<T, bool>> predicate, CancellationToken ct);
    Task AddAsync(T entity);
    Task AddAsync(T entity, CancellationToken ct);
    Task UpdateAsync(string id, T entity);
    Task UpdateAsync(string id, T entity, CancellationToken ct);
    Task DeleteAsync(string id);
    Task DeleteAsync(string id, CancellationToken ct);
    /// <summary>
    /// Physically removes the document (<c>DeleteOne</c>), bypassing the
    /// soft-delete flag. Reserved for GDPR erasure (`POST /api/me/purge`,
    /// ADR-0010). Admin <c>DELETE /api/users/{id}</c> stays soft-delete
    /// (<see cref="DeleteAsync(string)"/>).
    /// </summary>
    Task HardDeleteAsync(string id);
    Task HardDeleteAsync(string id, CancellationToken ct);
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate);
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken ct);

    Task<(IReadOnlyList<T> Items, long Total)> PagedAsync(
        Expression<Func<T, bool>> predicate,
        int page,
        int size,
        string? sortBy,
        bool desc,
        CancellationToken ct = default);

    Task<long> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
}
