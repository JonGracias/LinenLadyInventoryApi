using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Models;

namespace LinenLady.Inventory.Functions;

public sealed class GetItemImages
{
    private readonly ILogger _logger;

    public GetItemImages(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GetItemImages>();
    }

    [Function("GetItemImages")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}/images")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", cancellationToken);
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

        const string existsSql = @"
SELECT COUNT(1)
FROM inv.Inventory
WHERE InventoryId = @InventoryId AND IsDeleted = 0;
";

        const string imagesSql = @"
SELECT ImageId, ImagePath, IsPrimary, SortOrder
FROM inv.InventoryImage
WHERE InventoryId = @InventoryId
ORDER BY SortOrder, ImageId;
";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            // ensure item exists + not deleted
            using (var existsCmd = new SqlCommand(existsSql, conn) { CommandTimeout = 30 })
            {
                existsCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });
                var exists = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(cancellationToken)) > 0;

                if (!exists)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("Item not found.", cancellationToken);
                    return notFound;
                }
            }

            using var cmd = new SqlCommand(imagesSql, conn) { CommandTimeout = 30 };
            cmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var images = new List<InventoryImageDto>();

            while (await reader.ReadAsync(cancellationToken))
            {
                images.Add(new InventoryImageDto
                {
                    ImageId = reader.GetInt32(0),
                    ImagePath = reader.GetString(1),
                    IsPrimary = reader.GetBoolean(2),
                    SortOrder = reader.GetInt32(3),
                });
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(images, cancellationToken);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in GetItemImages.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GetItemImages.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
