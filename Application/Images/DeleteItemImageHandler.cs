using LinenLady.Inventory.Functions.Infrastructure.Sql;

namespace LinenLady.Inventory.Application.Images;

public sealed class DeleteItemImageHandler
{
    private readonly IInventoryImageRepository _repo;

    public DeleteItemImageHandler(IInventoryImageRepository repo)
    {
        _repo = repo;
    }

    public async Task<DeleteItemImageResult> Handle(
        int inventoryId,
        int imageId,
        CancellationToken ct)
    {
        if (!await _repo.ItemExists(inventoryId, ct))
            return DeleteItemImageResult.ItemNotFound;

        var wasPrimary = await _repo.IsPrimaryImage(inventoryId, imageId, ct);
        if (wasPrimary is null)
            return DeleteItemImageResult.ImageNotFound;

        var deleted = await _repo.DeleteImage(inventoryId, imageId, ct);
        if (!deleted)
            return DeleteItemImageResult.ImageNotFound;

        if (wasPrimary == true)
        {
            var newPrimaryId = await _repo.PickNewPrimaryImage(inventoryId, ct);
            if (newPrimaryId.HasValue)
                await _repo.SetPrimaryImage(inventoryId, newPrimaryId.Value, ct);
        }

        return DeleteItemImageResult.Deleted;
    }
}
