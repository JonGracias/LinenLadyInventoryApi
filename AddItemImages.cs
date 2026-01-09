using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Models;

namespace LinenLady.Inventory.Functions;

public sealed class AddItemImages
{
    private readonly ILogger _logger;

    public AddItemImages(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AddItemImages>();
    }

    public sealed record NewImageRequest(string ImagePath, bool? IsPrimary, int? SortOrder);
    public sealed record AddImagesRequest(List<NewImageRequest> Images);

    [Function("AddItemImages")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/images")] HttpRequestData req,
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
            _logger.LogError("Missing environment variable: SQL_CONNECTION_STRING");
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        AddImagesRequest? body;
        try { body = await req.ReadFromJsonAsync<AddImagesRequest>(ct); }
        catch { body = null; }

        if (body?.Images is null || body.Images.Count == 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Body must include images[] with at least one entry.", ct);
            return bad;
        }

        if (body.Images.Count > 20)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Too many images (max 20 per request).", ct);
            return bad;
        }

        // Basic validation first (blob-name only, no URL/SAS)
        foreach (var img in body.Images)
        {
            if (string.IsNullOrWhiteSpace(img.ImagePath))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Each image requires imagePath.", ct);
                return bad;
            }

            var p = img.ImagePath.Trim();

            if (p.Length > 1024)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("imagePath too long (max 1024).", ct);
                return bad;
            }

            // Option A: ImagePath must be a blob name only (no scheme, no SAS, no leading slash)
            if (p.Contains("://", StringComparison.OrdinalIgnoreCase) || p.Contains('?', StringComparison.Ordinal) || p.StartsWith("/", StringComparison.Ordinal))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("imagePath must be a blob name (no URL, no SAS token).", ct);
                return bad;
            }

            // Keep blob names in your convention
            if (!p.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("imagePath must start with 'images/'.", ct);
                return bad;
            }

            if (img.SortOrder is not null && img.SortOrder <= 0)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("sortOrder must be > 0 when provided.", ct);
                return bad;
            }
        }

        const string loadPublicIdSql = @"
SELECT PublicId
FROM inv.Inventory
WHERE InventoryId = @InventoryId AND IsDeleted = 0;";

        const string maxSortSql = @"
SELECT ISNULL(MAX(SortOrder), 0)
FROM inv.InventoryImage
WHERE InventoryId = @InventoryId;";

        const string clearPrimarySql = @"
UPDATE inv.InventoryImage
SET IsPrimary = 0
WHERE InventoryId = @InventoryId;";

        const string insertSql = @"
INSERT INTO inv.InventoryImage (InventoryId, ImagePath, IsPrimary, SortOrder)
OUTPUT INSERTED.ImageId
VALUES (@InventoryId, @ImagePath, @IsPrimary, @SortOrder);";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var tx = conn.BeginTransaction();

            // Load PublicId (also validates existence + not deleted)
            Guid publicId;
            using (var loadCmd = new SqlCommand(loadPublicIdSql, conn, tx) { CommandTimeout = 30 })
            {
                loadCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });

                var obj = await loadCmd.ExecuteScalarAsync(ct);
                if (obj is null || obj is DBNull)
                {
                    tx.Rollback();
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("Item not found.", ct);
                    return notFound;
                }

                publicId = (Guid)obj;
            }

            // Enforce that blobNames belong to this item: images/{PublicId:N}/...
            var expectedPrefix = $"images/{publicId:N}/";
            foreach (var img in body.Images)
            {
                var p = img.ImagePath.Trim();
                if (!p.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync($"imagePath must start with '{expectedPrefix}'.", ct);
                    return bad;
                }
            }

            int currentMaxSort;
            using (var maxCmd = new SqlCommand(maxSortSql, conn, tx) { CommandTimeout = 30 })
            {
                maxCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                currentMaxSort = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct));
            }

            var created = new List<InventoryImageDto>(body.Images.Count);

            foreach (var img in body.Images)
            {
                var isPrimary = img.IsPrimary ?? false;

                // If this image is primary, clear existing primary first (last primary wins).
                if (isPrimary)
                {
                    using var clearCmd = new SqlCommand(clearPrimarySql, conn, tx) { CommandTimeout = 30 };
                    clearCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                    await clearCmd.ExecuteNonQueryAsync(ct);
                }

                var sortOrder = img.SortOrder ?? (++currentMaxSort);
                var imagePath = img.ImagePath.Trim();

                using var insertCmd = new SqlCommand(insertSql, conn, tx) { CommandTimeout = 30 };
                insertCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                insertCmd.Parameters.Add(new SqlParameter("@ImagePath", System.Data.SqlDbType.NVarChar, 1024) { Value = imagePath });
                insertCmd.Parameters.Add(new SqlParameter("@IsPrimary", System.Data.SqlDbType.Bit) { Value = isPrimary });
                insertCmd.Parameters.Add(new SqlParameter("@SortOrder", System.Data.SqlDbType.Int) { Value = sortOrder });

                var imageId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync(ct));

                created.Add(new InventoryImageDto
                {
                    ImageId = imageId,
                    ImagePath = imagePath,
                    IsPrimary = isPrimary,
                    SortOrder = sortOrder
                });
            }

            tx.Commit();

            var resp = req.CreateResponse(HttpStatusCode.Created);
            await resp.WriteAsJsonAsync(new { inventoryId = id, images = created }, ct);
            return resp;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in AddItemImages.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in AddItemImages.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", ct);
            return err;
        }
    }
}
