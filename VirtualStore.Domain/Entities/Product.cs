using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VirtualStore.Domain.Entities;

public class Product : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "usd";
    public int StockQuantity { get; set; }
    public string? ImageUrl { get; set; }
    [BsonRepresentation(BsonType.ObjectId)]
    public string CategoryId { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public bool IsActive { get; set; } = true;
}