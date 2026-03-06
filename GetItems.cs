// GetItems.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Contracts;

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

        var query = ParseQuery(req.Url);

        var limit = Math.Clamp(TryGetInt(query, "limit") ?? 10, 1, 200);
        var page  = Math.Max(1, TryGetInt(query, "page") ?? 1);

        var status = (TryGetString(query, "status") ?? "all").Trim().ToLowerInvariant();
        var mode = status switch
        {
            "all"      => 0,
            "draft"    => 1,
            "active"   => 2,
            "featured" => 3,
            _          => -1
        };

        if (mode == -1)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid status. Use: all, draft, active, featured.", cancellationToken);
            return bad;
        }

        // Optional keyword-based category filter.
        // Searches against KeywordsJson stored in inv.InventoryAiMeta.
        // e.g. ?category=tablecloth  matches items whose KeywordsJson contains "tablecloth"
        var category = TryGetString(query, "category")?.Trim().ToLowerInvariant();
        var hasCategory = !string.IsNullOrWhiteSpace(category);

        var offset = (page - 1) * limit;

        // Category join clause — only emitted when ?category= is set.
        // Uses LIKE against the raw JSON string which is fast enough for admin use
        // and avoids OPENJSON overhead on every row.
        const string categoryJoin = """
            INNER JOIN inv.InventoryAiMeta m
                ON m.InventoryId = i.InventoryId
               AND LOWER(m.KeywordsJson) LIKE @CategoryPattern
            """;

        var statusClause = """
            i.IsDeleted = 0
            AND (
                @Mode = 0
                OR (@Mode = 1 AND i.IsDraft  = 1)
                OR (@Mode = 2 AND i.IsDraft  = 0 AND i.IsActive  = 1)
                OR (@Mode = 3 AND i.IsFeatured = 1)
            )
            """;

        var countSql = $"""
            SELECT COUNT_BIG(1)
            FROM inv.Inventory i
            {(hasCategory ? categoryJoin : "")}
            WHERE {statusClause};
            """;

        var pageSql = $"""
            WITH page AS (
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
                    i.IsFeatured,
                    i.CreatedAt,
                    i.UpdatedAt
                FROM inv.Inventory i
                {(hasCategory ? categoryJoin : "")}
                WHERE {statusClause}
                ORDER BY i.InventoryId DESC
                OFFSET @Offset ROWS
                FETCH NEXT @Limit ROWS ONLY
            )
            SELECT
                p.InventoryId,
                p.PublicId,
                p.Sku,
                p.Name,
                p.Description,
                p.QuantityOnHand,
                p.UnitPriceCents,
                p.IsActive,
                p.IsDraft,
                p.IsDeleted,
                p.IsFeatured,
                p.CreatedAt,
                p.UpdatedAt,
                img.ImageId,
                img.ImagePath,
                img.IsPrimary,
                img.SortOrder
            FROM page p
            LEFT JOIN inv.InventoryImage img
                ON img.InventoryId = p.InventoryId
            ORDER BY p.InventoryId DESC, img.SortOrder;
            """;

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            // 1) Total count
            long totalCount;
            using (var countCmd = new SqlCommand(countSql, conn) { CommandTimeout = 30 })
            {
                countCmd.Parameters.AddWithValue("@Mode", mode);
                if (hasCategory)
                    countCmd.Parameters.AddWithValue("@CategoryPattern", $"%{category}%");

                var scalar = await countCmd.ExecuteScalarAsync(cancellationToken);
                totalCount = scalar is null or DBNull ? 0L : Convert.ToInt64(scalar);
            }

            // Clamp page if past the end
            var totalPages = (int)Math.Max(1, (totalCount + limit - 1) / limit);
            if (page > totalPages) page = totalPages;
            offset = (page - 1) * limit;

            // 2) Page query
            using var cmd = new SqlCommand(pageSql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@Mode",   mode);
            cmd.Parameters.AddWithValue("@Limit",  limit);
            cmd.Parameters.AddWithValue("@Offset", offset);
            if (hasCategory)
                cmd.Parameters.AddWithValue("@CategoryPattern", $"%{category}%");

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var itemsById = new Dictionary<int, InventoryItemDto>();

            // Column ordinals — keep aligned with SELECT list above
            const int O_InventoryId    = 0;
            const int O_PublicId       = 1;
            const int O_Sku            = 2;
            const int O_Name           = 3;
            const int O_Description    = 4;
            const int O_QuantityOnHand = 5;
            const int O_UnitPriceCents = 6;
            const int O_IsActive       = 7;
            const int O_IsDraft        = 8;
            const int O_IsDeleted      = 9;
            const int O_IsFeatured     = 10;
            const int O_CreatedAt      = 11;
            const int O_UpdatedAt      = 12;
            const int O_ImageId        = 13;
            const int O_ImagePath      = 14;
            const int O_IsPrimary      = 15;
            const int O_SortOrder      = 16;

            while (await reader.ReadAsync(cancellationToken))
            {
                var inventoryId = reader.GetInt32(O_InventoryId);

                if (!itemsById.TryGetValue(inventoryId, out var item))
                {
                    item = new InventoryItemDto
                    {
                        InventoryId    = inventoryId,
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

                    itemsById.Add(inventoryId, item);
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

            var items = itemsById.Values.OrderByDescending(i => i.InventoryId).ToList();

            var payload = new
            {
                items,
                page,
                limit,
                totalCount,
                totalPages,
                status,
                category = category ?? ""
            };

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(payload, cancellationToken);
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

    private static Dictionary<string, string> ParseQuery(Uri url)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = url.Query;
        if (string.IsNullOrWhiteSpace(q)) return dict;
        if (q.StartsWith("?")) q = q[1..];
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv  = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            if (!string.IsNullOrWhiteSpace(key)) dict[key] = val;
        }
        return dict;
    }

    private static int? TryGetInt(Dictionary<string, string> query, string key)
        => query.TryGetValue(key, out var s) && int.TryParse(s, out var n) ? n : null;

    private static string? TryGetString(Dictionary<string, string> query, string key)
        => query.TryGetValue(key, out var s) ? s : null;
}