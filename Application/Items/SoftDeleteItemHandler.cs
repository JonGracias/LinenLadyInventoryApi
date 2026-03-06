// Application/Items/SoftDeleteItemHandler.cs
using LinenLady.Inventory.Functions.Contracts;
using LinenLady.Inventory.Functions.Infrastructure.Sql;

namespace LinenLady.Inventory.Application.Items;

public sealed class SoftDeleteItemHandler
{
    private readonly IInventoryRepository _repo;
    private readonly IInventoryImageRepository _imageRepo;

    public SoftDeleteItemHandler(
        IInventoryRepository repo,
        IInventoryImageRepository imageRepo)
    {
        _repo = repo;
        _imageRepo = imageRepo;
    }

    public async Task<SoftDeleteItemResult> Handle(int inventoryId, CancellationToken ct)
    {
        var exists = await _imageRepo.ItemExists(inventoryId, ct);
        if (!exists)
            return SoftDeleteItemResult.NotFound;

        var deleted = await _repo.SoftDelete(inventoryId, ct);
        return deleted
            ? SoftDeleteItemResult.Deleted
            : SoftDeleteItemResult.NotFound;
    }
}