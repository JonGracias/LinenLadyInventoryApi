// Models/InventoryImageDto.cs
namespace LinenLady.Inventory.Functions.Models;

public sealed class InventoryImageDto
{
    public int ImageId { get; set; }
    public string ImagePath { get; set; } = "";
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }
}
