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

    // ── Shared ordinals — keep aligned with the SELECT list in both queries ──
    private const int O_InventoryId    = 0;
    private const int O_PublicId       = 1;
    private const int O_Sku            = 2;
    private const int O_Name           = 3;
    private const int O_Description    = 4;
    private const int O_QuantityOnHand = 5;
    private const int O_UnitPriceCents = 6;
    private const int O_IsActive       = 7;
    private const int O_IsDraft        = 8;
    private const int O_IsDeleted      = 9;
    private const int O_IsFeatured     = 10;
    private const int O_CreatedAt      = 11;
    private const int O_UpdatedAt      = 12;
    private const int O_ImageId        = 13;
    private const int O_ImagePath      = 14;
    private const int O_IsPrimary      = 15;
    private const int O_SortOrder      = 16;

    private const string SelectColumns = """
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
        i.IsFeatured,
        i.CreatedAt,
        i.UpdatedAt,
        img.ImageId,
        img.ImagePath,
        img.IsPrimary,
        img.SortOrder
        """;

    // ── GET /api/items/sku/{sku} ───────────────────────────────────────────────

    [Function("GetItemBySku")]
    public async Task<HttpResponseData> RunBySku(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/sku/{sku}")] HttpRequestData req,
        string sku,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid sku.", cancellationToken);
            return bad;
        }

        var sql = $"""
            SELECT {SelectColumns}
            FROM inv.Inventory i
            LEFT JOIN inv.InventoryImage img ON img.InventoryId = i.InventoryId
            WHERE i.Sku = @Sku AND i.IsDeleted = 0
            ORDER BY img.SortOrder;
            """;

        return await RunQuery(req, sql, cmd =>
            cmd.Parameters.Add("@Sku", System.Data.SqlDbType.NVarChar, 64).Value = sku.Trim(),
            cancellationToken);
    }

    // ── GET /api/items/{id:int} ───────────────────────────────────────────────

    [Function("GetItemById")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", cancellationToken);
            return bad;
        }

        var sql = $"""
            SELECT {SelectColumns}
            FROM inv.Inventory i
            LEFT JOIN inv.InventoryImage img ON img.InventoryId = i.InventoryId
            WHERE i.InventoryId = @InventoryId AND i.IsDeleted = 0
            ORDER BY img.SortOrder;
            """;

        return await RunQuery(req, sql, cmd =>
            cmd.Parameters.AddWithValue("@InventoryId", id),
            cancellationToken);
    }

    // ── Shared execution ──────────────────────────────────────────────────────

    private async Task<HttpResponseData> RunQuery(
        HttpRequestData req,
        string sql,
        Action<SqlCommand> bindParams,
        CancellationToken ct)
    {
        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogError("Missing environment variable: SQL_CONNECTION_STRING");
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            bindParams(cmd);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            InventoryItemDto? item = null;

            while (await reader.ReadAsync(ct))
            {
                if (item is null)
                {
                    item = new InventoryItemDto
                    {
                        InventoryId    = reader.GetInt32(O_InventoryId),
                        PublicId       = reader.GetGuid(O_PublicId),
                        Sku            = reader.GetString(O_Sku),
                        Name           = reader.GetString(O_Name),
                        Description    = reader.IsDBNull(O_Description) ? null : reader.GetString(O_Description),
                        QuantityOnHand = reader.GetInt32(O_QuantityOnHand),
                        UnitPriceCents = reader.GetInt32(O_UnitPriceCents),
                        IsActive       = reader.GetBoolean(O_IsActive),
                        IsDraft        = reader.GetBoolean(O_IsDraft),
                        IsDeleted      = reader.GetBoolean(O_IsDeleted),
                        IsFeatured     = reader.GetBoolean(O_IsFeatured),
                        CreatedAt      = reader.GetDateTime(O_CreatedAt),
                        UpdatedAt      = reader.GetDateTime(O_UpdatedAt),
                    };
                }

                if (!reader.IsDBNull(O_ImageId))
                {
                    item.Images.Add(new InventoryImageDto
                    {
                        ImageId   = reader.GetInt32(O_ImageId),
                        ImagePath = reader.GetString(O_ImagePath),
                        IsPrimary = reader.GetBoolean(O_IsPrimary),
                        SortOrder = reader.GetInt32(O_SortOrder),
                    });
                }
            }

            if (item is null)
            {
                var nf = req.CreateResponse(HttpStatusCode.NotFound);
                await nf.WriteStringAsync("Item not found.", ct);
                return nf;
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(item, ct);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in GetItemById.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GetItemById.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", ct);
            return err;
        }
    }
}