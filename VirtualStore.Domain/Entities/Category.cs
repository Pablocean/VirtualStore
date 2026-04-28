using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VirtualStore.Domain.Entities;

public class Category : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ParentCategoryId { get; set; }
    public bool IsActive { get; set; } = true;
}