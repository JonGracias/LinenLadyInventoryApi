// Infrastructure/Sql/UpdateItemFields.cs
namespace LinenLady.Inventory.Functions.Infrastructure.Sql;

public sealed class UpdateItemFields
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int UnitPriceCents { get; set; }
    public int QuantityOnHand { get; set; }
    public bool IsActive { get; set; }
    public bool IsDraft { get; set; }
    public bool IsFeatured { get; set; }
}