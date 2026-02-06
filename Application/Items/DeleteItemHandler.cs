using LinenLady.Inventory.Functions.Infrastructure.Sql;

namespace LinenLady.Inventory.Application.Items;

public enum DeleteItemResult
{
    Deleted,
    NotFound
}

public sealed class DeleteItemHandler
{
    private readonly IInventoryRepository _repo;

    public DeleteItemHandler(IInventoryRepository repo)
    {
        _repo = repo;
    }

    public async Task<DeleteItemResult> Handle(int inventoryId, CancellationToken ct)
    {
        var deleted = await _repo.SoftDelete(inventoryId, ct);
        return deleted ? DeleteItemResult.Deleted : DeleteItemResult.NotFound;
    }
}
