// GetItemById.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Contracts;

namespace LinenLady.Inventory.Functions;

public sealed class GetItemById
{
    private readonly ILogger _logger;

    public GetItemById(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GetItemById>();
    }

    [Function("GetItemById")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
            await badReq.WriteStringAsync("Invalid id.", cancellationToken);
            return badReq;
        }

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
            i.PublicId,
            i.Sku,
            i.Name,
            i.Description,
            i.QuantityOnHand,
            i.UnitPriceCents,
            i.IsActive,
            i.IsDraft,
            i.IsDeleted,
            i.CreatedAt,
            i.UpdatedAt,
            img.ImageId,
            img.ImagePath,
            img.IsPrimary,
            img.SortOrder
        FROM inv.Inventory i
        LEFT JOIN inv.InventoryImage img
            ON img.InventoryId = i.InventoryId
        WHERE i.InventoryId = @InventoryId AND i.IsDeleted = 0
        ORDER BY img.SortOrder;";


        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            using var cmd = new SqlCommand(sql, conn)
            {
                CommandTimeout = 30
            };
            cmd.Parameters.AddWithValue("@InventoryId", id);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            // Ordinals (keep aligned to SELECT list)
            const int O_InventoryId = 0;
            const int O_PublicId = 1;
            const int O_Sku = 2;
            const int O_Name = 3;
            const int O_Description = 4;
            const int O_QuantityOnHand = 5;
            const int O_UnitPriceCents = 6;
            const int O_IsActive = 7;
            const int O_IsDraft = 8;
            const int O_IsDeleted = 9;
            const int O_CreatedAt = 10;
            const int O_UpdatedAt = 11;

            const int O_ImageId = 12;
            const int O_ImagePath = 13;
            const int O_IsPrimary = 14;
            const int O_SortOrder = 15;


            InventoryItemDto? item = null;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (item is null)
                {
                    item = new InventoryItemDto
                    {
                        InventoryId = reader.GetInt32(O_InventoryId),
                        PublicId = reader.GetGuid(O_PublicId),
                        Sku = reader.GetString(O_Sku),
                        Name = reader.GetString(O_Name),
                        Description = reader.IsDBNull(O_Description) ? null : reader.GetString(O_Description),
                        QuantityOnHand = reader.GetInt32(O_QuantityOnHand),
                        UnitPriceCents = reader.GetInt32(O_UnitPriceCents),

                        IsActive = reader.GetBoolean(O_IsActive),
                        IsDraft = reader.GetBoolean(O_IsDraft),
                        IsDeleted = reader.GetBoolean(O_IsDeleted),
                        CreatedAt = reader.GetDateTime(O_CreatedAt),
                        UpdatedAt = reader.GetDateTime(O_UpdatedAt),

                    };

                }

                // LEFT JOIN can produce nulls for image columns
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

            if (item is null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("Item not found.", cancellationToken);
                return notFound;
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(item, cancellationToken);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in GetItemById.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GetItemById.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
