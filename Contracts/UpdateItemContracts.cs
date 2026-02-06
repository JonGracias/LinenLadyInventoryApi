using System.Text.Json.Serialization;

namespace LinenLady.Inventory.Contracts;

public static class UpdateItemContracts
{
    // If a property is omitted => no change.
    // If description is present and null => clear it.
    public sealed record UpdateItemRequest(
        string? Sku,
        string? Name,
        string? Description,
        int? QuantityOnHand,
        int? UnitPriceCents
    );
}
