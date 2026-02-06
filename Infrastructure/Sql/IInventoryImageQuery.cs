using LinenLady.Inventory.Functions.Contracts;

namespace LinenLady.Inventory.Functions.Infrastructure.Sql;

public interface IInventoryImagesQuery
{
    Task<bool> ItemExists(int inventoryId, CancellationToken ct);
    Task<IReadOnlyList<InventoryImageDto>> GetImages(int inventoryId, CancellationToken ct);
}
