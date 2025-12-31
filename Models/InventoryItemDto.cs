// Models/InventoryItemDto.cs
namespace LinenLady.Inventory.Functions.Models;

public sealed class InventoryItemDto
{
    public int InventoryId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    public int QuantityOnHand { get; set; }
    public int UnitPriceCents { get; set; }

    public List<InventoryImageDto> Images { get; } = new();
}
