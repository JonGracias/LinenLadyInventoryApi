using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class DeleteItemImage
{
    private readonly ILogger _logger;

    public DeleteItemImage(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<DeleteItemImage>();
    }

    [Function("DeleteItemImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "items/{id:int}/images/{imageId:int}")] HttpRequestData req,
        int id,
        int imageId,
        CancellationToken cancellationToken)
    {
        if (id <= 0 || imageId <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id or imageId.", cancellationToken);
            return bad;
        }

        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogError("Missing environment variable: SQL_CONNECTION_STRING");
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", cancellationToken);
            return bad;
        }

        const string itemExistsSql = @"
SELECT COUNT(1)
FROM inv.Inventory
WHERE InventoryId = @InventoryId AND IsDeleted = 0;
";

        const string imageInfoSql = @"
SELECT IsPrimary
FROM inv.InventoryImage
WHERE ImageId = @ImageId AND InventoryId = @InventoryId;
";

        const string deleteSql = @"
DELETE FROM inv.InventoryImage
WHERE ImageId = @ImageId AND InventoryId = @InventoryId;

SELECT @@ROWCOUNT;
";

        const string pickNewPrimarySql = @"
SELECT TOP 1 ImageId
FROM inv.InventoryImage
WHERE InventoryId = @InventoryId
ORDER BY SortOrder, ImageId;
";

        const string setPrimarySql = @"
UPDATE inv.InventoryImage
SET IsPrimary = CASE WHEN ImageId = @PrimaryImageId THEN 1 ELSE 0 END
WHERE InventoryId = @InventoryId;
";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            using var tx = conn.BeginTransaction();

            // item exists + not deleted
            using (var itemCmd = new SqlCommand(itemExistsSql, conn, tx) { CommandTimeout = 30 })
            {
                itemCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                var exists = Convert.ToInt32(await itemCmd.ExecuteScalarAsync(cancellationToken)) > 0;
                if (!exists)
                {
                    tx.Rollback();
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("Item not found.", cancellationToken);
                    return notFound;
                }
            }

            bool? wasPrimary = null;
            using (var infoCmd = new SqlCommand(imageInfoSql, conn, tx) { CommandTimeout = 30 })
            {
                infoCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                infoCmd.Parameters.Add(new SqlParameter("@ImageId", System.Data.SqlDbType.Int) { Value = imageId });

                var result = await infoCmd.ExecuteScalarAsync(cancellationToken);
                if (result is null || result is DBNull)
                {
                    tx.Rollback();
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("Image not found for this item.", cancellationToken);
                    return notFound;
                }

                wasPrimary = Convert.ToBoolean(result);
            }

            int deletedRows;
            using (var delCmd = new SqlCommand(deleteSql, conn, tx) { CommandTimeout = 30 })
            {
                delCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                delCmd.Parameters.Add(new SqlParameter("@ImageId", System.Data.SqlDbType.Int) { Value = imageId });
                deletedRows = Convert.ToInt32(await delCmd.ExecuteScalarAsync(cancellationToken));
            }

            if (deletedRows == 0)
            {
                tx.Rollback();
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("Image not found for this item.", cancellationToken);
                return notFound;
            }

            // If we deleted the primary, promote a new one (if any remain)
            if (wasPrimary == true)
            {
                int? newPrimaryId = null;

                using (var pickCmd = new SqlCommand(pickNewPrimarySql, conn, tx) { CommandTimeout = 30 })
                {
                    pickCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                    var picked = await pickCmd.ExecuteScalarAsync(cancellationToken);
                    if (picked is not null && picked is not DBNull)
                        newPrimaryId = Convert.ToInt32(picked);
                }

                if (newPrimaryId.HasValue)
                {
                    using var setCmd = new SqlCommand(setPrimarySql, conn, tx) { CommandTimeout = 30 };
                    setCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                    setCmd.Parameters.Add(new SqlParameter("@PrimaryImageId", System.Data.SqlDbType.Int) { Value = newPrimaryId.Value });
                    await setCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            tx.Commit();
            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in DeleteItemImage.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in DeleteItemImage.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
