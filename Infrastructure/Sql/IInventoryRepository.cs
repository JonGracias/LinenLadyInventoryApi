using LinenLady.Inventory.Functions.Contracts;

namespace LinenLady.Inventory.Functions.Infrastructure.Sql;

public interface IInventoryRepository
{
    Task<bool> SoftDelete(int inventoryId, CancellationToken ct);
    Task<InventoryItemDto?> GetById(int inventoryId, CancellationToken ct);
    Task<bool> Update(int inventoryId, UpdateItemFields fields, CancellationToken ct);
}

