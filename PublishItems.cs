// PublishItems.cs
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class PublishItems
{
    private readonly ILogger _logger;

    public PublishItems(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<PublishItems>();
    }

    // POST /api/items/{id}/publish
    [Function("PublishItem")]
    public async Task<HttpResponseData> Publish(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/publish")] HttpRequestData req,
        int id,
        CancellationToken ct)
        => await SetPublishedState(req, id, publish: true, ct);

    // POST /api/items/{id}/unpublish
    [Function("UnpublishItem")]
    public async Task<HttpResponseData> Unpublish(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/unpublish")] HttpRequestData req,
        int id,
        CancellationToken ct)
        => await SetPublishedState(req, id, publish: false, ct);

    private async Task<HttpResponseData> SetPublishedState(HttpRequestData req, int id, bool publish, CancellationToken ct)
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

        // Optional body flags (future-proof) - safe to ignore for now.
        // Supports: { "forcePrimaryImage": true }
        bool forcePrimaryImage = true;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("forcePrimaryImage", out var f) &&
                (f.ValueKind == JsonValueKind.True || f.ValueKind == JsonValueKind.False))
            {
                forcePrimaryImage = f.GetBoolean();
            }
        }
        catch
        {
            // ignore body parse errors; endpoint still works
        }

        const string loadSql = @"
SELECT
    i.InventoryId,
    i.PublicId,
    i.Sku,
    i.Name,
    i.Description,
    i.QuantityOnHand,
    i.UnitPriceCents,
    i.IsActive,
    i.IsDraft,
    i.IsDeleted
FROM inv.Inventory i
WHERE i.InventoryId = @Id AND i.IsDeleted = 0;
";

        const string imageCountSql = @"
SELECT COUNT(1)
FROM inv.InventoryImage
WHERE InventoryId = @Id;
";

        const string hasPrimarySql = @"
SELECT COUNT(1)
FROM inv.InventoryImage
WHERE InventoryId = @Id AND IsPrimary = 1;
";

        // Picks the first image by SortOrder/Id and makes it primary, clearing others.
        const string ensurePrimarySql = @"
;WITH cte AS (
    SELECT TOP (1) ImageId
    FROM inv.InventoryImage
    WHERE InventoryId = @Id
    ORDER BY SortOrder ASC, ImageId ASC
)
UPDATE inv.InventoryImage
SET IsPrimary = CASE WHEN ImageId = (SELECT ImageId FROM cte) THEN 1 ELSE 0 END
WHERE InventoryId = @Id;
";

        const string publishSql = @"
SET NOCOUNT ON;
UPDATE inv.Inventory
SET
    IsActive = @IsActive,
    IsDraft  = @IsDraft,
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

            using var tx = conn.BeginTransaction();

            // Load item
            Guid publicId;
            string sku;
            string name;
            int unitPriceCents;
            bool isDraft;

            using (var loadCmd = new SqlCommand(loadSql, conn, tx) { CommandTimeout = 30 })
            {
                loadCmd.Parameters.AddWithValue("@Id", id);

                using var r = await loadCmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    tx.Rollback();
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteStringAsync("Item not found.", ct);
                    return nf;
                }

                publicId = r.GetGuid(1);
                sku = r.GetString(2);
                name = r.GetString(3);
                unitPriceCents = r.GetInt32(6);
                isDraft = r.GetBoolean(8);
            }

            if (publish)
            {
                // Publish validation rules
                if (string.IsNullOrWhiteSpace(name) || name.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("Cannot publish: Name must be set (not 'Draft').", ct);
                    return bad;
                }

                if (unitPriceCents <= 0)
                {
                    tx.Rollback();
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("Cannot publish: UnitPriceCents must be > 0.", ct);
                    return bad;
                }

                int imageCount;
                using (var imgCountCmd = new SqlCommand(imageCountSql, conn, tx) { CommandTimeout = 30 })
                {
                    imgCountCmd.Parameters.AddWithValue("@Id", id);
                    imageCount = Convert.ToInt32(await imgCountCmd.ExecuteScalarAsync(ct));
                }

                if (imageCount <= 0)
                {
                    tx.Rollback();
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("Cannot publish: item must have at least one image.", ct);
                    return bad;
                }

                if (forcePrimaryImage)
                {
                    int primaryCount;
                    using (var primaryCmd = new SqlCommand(hasPrimarySql, conn, tx) { CommandTimeout = 30 })
                    {
                        primaryCmd.Parameters.AddWithValue("@Id", id);
                        primaryCount = Convert.ToInt32(await primaryCmd.ExecuteScalarAsync(ct));
                    }

                    if (primaryCount <= 0)
                    {
                        using var ensureCmd = new SqlCommand(ensurePrimarySql, conn, tx) { CommandTimeout = 30 };
                        ensureCmd.Parameters.AddWithValue("@Id", id);
                        await ensureCmd.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            // Apply state change
            object payload;

            using (var cmd = new SqlCommand(publishSql, conn, tx) { CommandTimeout = 30 })
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@IsActive", publish ? 1 : 0);
                cmd.Parameters.AddWithValue("@IsDraft", publish ? 0 : 1);

                using var r2 = await cmd.ExecuteReaderAsync(ct);

                // If your batch returns an empty/rowcount result set first, skip it
                while (r2.FieldCount == 0 && await r2.NextResultAsync(ct)) { }

                if (!await r2.ReadAsync(ct))
                {
                    tx.Rollback();
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteStringAsync("Item not found.", ct);
                    return nf;
                }

                payload = new
                {
                    inventoryId = r2.GetInt32(0),
                    publicId = r2.GetGuid(1).ToString("N"),
                    sku = r2.GetString(2),
                    name = r2.GetString(3),
                    description = r2.IsDBNull(4) ? null : r2.GetString(4),
                    quantityOnHand = r2.GetInt32(5),
                    unitPriceCents = r2.GetInt32(6),
                    isActive = r2.GetBoolean(7),
                    isDraft = r2.GetBoolean(8),
                    isDeleted = r2.GetBoolean(9),
                    createdAt = r2.GetDateTime(10),
                    updatedAt = r2.GetDateTime(11)
                };
            } // reader + command disposed here

            tx.Commit();

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(payload, ct);
            return ok;

        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in PublishItems.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in PublishItems.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", ct);
            return err;
        }
    }
}
