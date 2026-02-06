namespace LinenLady.Inventory.Functions.Infrastructure.Sql;

public interface IInventoryRepository
{
    Task<bool> SoftDelete(int inventoryId, CancellationToken ct);
}
