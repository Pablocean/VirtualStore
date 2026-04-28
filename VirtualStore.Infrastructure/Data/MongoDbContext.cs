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
}