// GetItems.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Models;

namespace LinenLady.Inventory.Functions;

public sealed class GetItems
{
    private readonly ILogger _logger;

    public GetItems(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GetItems>();
    }

    [Function("GetItems")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogError("Missing environment variable: SQL_CONNECTION_STRING");
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", cancellationToken);
            return bad;
        }

        const string sql = @"
SELECT
    i.InventoryId,
    i.Sku,
    i.Name,
    i.Description,
    i.QuantityOnHand,
    i.UnitPriceCents,
    img.ImageId,
    img.ImagePath,
    img.IsPrimary,
    img.SortOrder
FROM inv.Inventory i
LEFT JOIN inv.InventoryImage img
    ON img.InventoryId = i.InventoryId
ORDER BY i.InventoryId, img.SortOrder;
";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            using var cmd = new SqlCommand(sql, conn)
            {
                CommandTimeout = 30
            };

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            // InventoryId -> Item (with Images list)
            var items = new Dictionary<int, InventoryItemDto>();

            // Ordinals (keep aligned to SELECT list)
            const int O_InventoryId = 0;
            const int O_Sku = 1;
            const int O_Name = 2;
            const int O_Description = 3;
            const int O_QuantityOnHand = 4;
            const int O_UnitPriceCents = 5;

            const int O_ImageId = 6;
            const int O_ImagePath = 7;
            const int O_IsPrimary = 8;
            const int O_SortOrder = 9;

            while (await reader.ReadAsync(cancellationToken))
            {
                var inventoryId = reader.GetInt32(O_InventoryId);

                if (!items.TryGetValue(inventoryId, out var item))
                {
                    item = new InventoryItemDto
                    {
                        InventoryId = inventoryId,
                        Sku = reader.GetString(O_Sku),
                        Name = reader.GetString(O_Name),
                        Description = reader.IsDBNull(O_Description) ? null : reader.GetString(O_Description),
                        QuantityOnHand = reader.GetInt32(O_QuantityOnHand),
                        UnitPriceCents = reader.GetInt32(O_UnitPriceCents),
                    };

                    items.Add(inventoryId, item);
                }

                // If there is an image row (LEFT JOIN can produce nulls)
                if (!reader.IsDBNull(O_ImageId))
                {
                    item.Images.Add(new InventoryImageDto
                    {
                        ImageId = reader.GetInt32(O_ImageId),
                        ImagePath = reader.GetString(O_ImagePath),
                        IsPrimary = reader.GetBoolean(O_IsPrimary),
                        SortOrder = reader.GetInt32(O_SortOrder),
                    });
                }
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(items.Values.OrderBy(i => i.InventoryId), cancellationToken);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in GetItems.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GetItems.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
