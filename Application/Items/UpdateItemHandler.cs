using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Contracts;

namespace LinenLady.Inventory.Application.Items;

public sealed class UpdateItemHandler
{
    private readonly ILogger<UpdateItemHandler> _logger;

    public UpdateItemHandler(ILogger<UpdateItemHandler> logger)
    {
        _logger = logger;
    }

    public async Task<InventoryItemDto> HandleAsync(int id, JsonElement root, CancellationToken ct)
    {
        if (id <= 0) throw new ArgumentException("Invalid id.");

        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("Server misconfigured: missing SQL_CONNECTION_STRING.");

        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Body must be a JSON object.");

        // Optional fields: update only what is present. :contentReference[oaicite:4]{index=4}
        bool hasSku = TryGetString(root, "sku", out var sku, allowNull: false);
        bool hasName = TryGetString(root, "name", out var name, allowNull: false);

        // Description can be explicitly set to null to clear it. :contentReference[oaicite:5]{index=5}
        bool hasDescription = TryGetString(root, "description", out var description, allowNull: true);

        bool hasQoh = TryGetInt(root, "quantityOnHand", out var quantityOnHand);
        bool hasPrice = TryGetInt(root, "unitPriceCents", out var unitPriceCents);

        if (!(hasSku || hasName || hasDescription || hasQoh || hasPrice))
            throw new ArgumentException("No updatable fields provided.");

        // Validation (same rules as current) :contentReference[oaicite:6]{index=6}
        if (hasSku)
        {
            sku = sku!.Trim();
            if (sku.Length == 0 || sku.Length > 64)
                throw new ArgumentException("sku must be 1..64 characters.");
        }

        if (hasName)
        {
            name = name!.Trim();
            if (name.Length == 0 || name.Length > 255)
                throw new ArgumentException("name must be 1..255 characters.");
        }

        if (hasDescription && description is not null && description.Length > 4000)
            throw new ArgumentException("description too long (max 4000 characters).");

        if (hasQoh && quantityOnHand < 0)
            throw new ArgumentException("quantityOnHand must be >= 0.");

        if (hasPrice && unitPriceCents < 0)
            throw new ArgumentException("unitPriceCents must be >= 0.");

        const string updateSql = @"
UPDATE inv.Inventory
SET
    Sku = COALESCE(@Sku, Sku),
    Name = COALESCE(@Name, Name),
    Description = CASE WHEN @DescriptionIsSet = 1 THEN @Description ELSE Description END,
    QuantityOnHand = COALESCE(@QuantityOnHand, QuantityOnHand),
    UnitPriceCents = COALESCE(@UnitPriceCents, UnitPriceCents),
    UpdatedAt = SYSUTCDATETIME()
WHERE InventoryId = @Id AND IsDeleted = 0;

SELECT
    InventoryId, PublicId, Sku, Name, Description, QuantityOnHand, UnitPriceCents,
    IsActive, IsDraft, IsDeleted, CreatedAt, UpdatedAt
FROM inv.Inventory
WHERE InventoryId = @Id AND IsDeleted = 0;
";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(updateSql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@Id", id);

            cmd.Parameters.AddWithValue("@Sku", (object?)(hasSku ? sku : null) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Name", (object?)(hasName ? name : null) ?? DBNull.Value);

            cmd.Parameters.AddWithValue("@DescriptionIsSet", hasDescription ? 1 : 0);
            cmd.Parameters.AddWithValue("@Description", hasDescription ? (object?)description ?? DBNull.Value : DBNull.Value);

            cmd.Parameters.AddWithValue("@QuantityOnHand", hasQoh ? quantityOnHand : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@UnitPriceCents", hasPrice ? unitPriceCents : (object)DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                throw new KeyNotFoundException("Item not found.");

            return new InventoryItemDto
            {
                InventoryId = reader.GetInt32(0),
                PublicId = reader.GetGuid(1),
                Sku = reader.GetString(2),
                Name = reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                QuantityOnHand = reader.GetInt32(5),
                UnitPriceCents = reader.GetInt32(6),
                IsActive = reader.GetBoolean(7),
                IsDraft = reader.GetBoolean(8),
                IsDeleted = reader.GetBoolean(9),
                CreatedAt = reader.GetDateTime(10),
                UpdatedAt = reader.GetDateTime(11)
            };
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in UpdateItemHandler.");
            throw new InvalidOperationException("Database error.");
        }
    }

    private static bool TryGetString(JsonElement obj, string prop, out string? value, bool allowNull)
    {
        value = null;
        if (!obj.TryGetProperty(prop, out var el)) return false;

        if (el.ValueKind == JsonValueKind.Null)
        {
            if (!allowNull) return false;
            value = null;
            return true;
        }

        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return true;
    }

    private static bool TryGetInt(JsonElement obj, string prop, out int value)
    {
        value = default;
        if (!obj.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetInt32(out value);
    }
}
