// UpdateItem.cs
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class UpdateItem
{
    private readonly ILogger _logger;

    public UpdateItem(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<UpdateItem>();
    }

    [Function("UpdateItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "put", Route = "items/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", ct);
            return bad;
        }

        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        JsonDocument? doc = null;
        try { doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        using (doc)
        {
            var root = doc!.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Body must be a JSON object.", ct);
                return bad;
            }

            // Optional fields: update only what is present.
            bool hasSku = TryGetString(root, "sku", out var sku, allowNull: false);
            bool hasName = TryGetString(root, "name", out var name, allowNull: false);

            // Description can be explicitly set to null to clear it.
            bool hasDescription = TryGetString(root, "description", out var description, allowNull: true);

            bool hasQoh = TryGetInt(root, "quantityOnHand", out var quantityOnHand);
            bool hasPrice = TryGetInt(root, "unitPriceCents", out var unitPriceCents);

            if (!(hasSku || hasName || hasDescription || hasQoh || hasPrice))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("No updatable fields provided.", ct);
                return bad;
            }

            // Validation
            if (hasSku)
            {
                sku = sku!.Trim();
                if (sku.Length == 0 || sku.Length > 64)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("sku must be 1..64 characters.", ct);
                    return bad;
                }
            }

            if (hasName)
            {
                name = name!.Trim();
                if (name.Length == 0 || name.Length > 255)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("name must be 1..255 characters.", ct);
                    return bad;
                }
            }

            if (hasDescription && description is not null && description.Length > 4000)
            {
                // NVARCHAR(MAX) technically supports more, but keep it bounded for safety.
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("description too long (max 4000 characters).", ct);
                return bad;
            }

            if (hasQoh && quantityOnHand < 0)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("quantityOnHand must be >= 0.", ct);
                return bad;
            }

            if (hasPrice && unitPriceCents < 0)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("unitPriceCents must be >= 0.", ct);
                return bad;
            }

            // Update: only applies to non-deleted items. Always bumps UpdatedAt.
            // Description needs special handling so that:
            // - missing property => no change
            // - "description": null => clear
            // - "description": "text" => set
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

                cmd.Parameters.AddWithValue("@Sku", (object?) (hasSku ? sku : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Name", (object?) (hasName ? name : null) ?? DBNull.Value);

                cmd.Parameters.AddWithValue("@DescriptionIsSet", hasDescription ? 1 : 0);
                cmd.Parameters.AddWithValue("@Description", hasDescription ? (object?)description ?? DBNull.Value : DBNull.Value);

                cmd.Parameters.AddWithValue("@QuantityOnHand", hasQoh ? quantityOnHand : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@UnitPriceCents", hasPrice ? unitPriceCents : (object)DBNull.Value);

                using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                {
                    // Either not found or IsDeleted=1
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteStringAsync("Item not found.", ct);
                    return nf;
                }

                var payload = new
                {
                    inventoryId = reader.GetInt32(0),
                    publicId = reader.GetGuid(1).ToString("N"),
                    sku = reader.GetString(2),
                    name = reader.GetString(3),
                    description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    quantityOnHand = reader.GetInt32(5),
                    unitPriceCents = reader.GetInt32(6),
                    isActive = reader.GetBoolean(7),
                    isDraft = reader.GetBoolean(8),
                    isDeleted = reader.GetBoolean(9),
                    createdAt = reader.GetDateTime(10),
                    updatedAt = reader.GetDateTime(11)
                };

                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteAsJsonAsync(payload, ct);
                return ok;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "SQL error in UpdateItem.");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Database error.", ct);
                return err;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in UpdateItem.");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Server error.", ct);
                return err;
            }
        }
    }

    private static bool TryGetString(JsonElement obj, string prop, out string? value, bool allowNull)
    {
        value = null;
        if (!obj.TryGetProperty(prop, out var el)) return false;

        if (el.ValueKind == JsonValueKind.Null)
        {
            if (!allowNull) return false; // property present but null not allowed -> treat as absent
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
