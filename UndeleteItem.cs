// UndeleteItem.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class UndeleteItem
{
    private readonly ILogger _logger;

    public UndeleteItem(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<UndeleteItem>();
    }

    [Function("UndeleteItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "items/{id:int}/undelete")] HttpRequestData req,
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

        const string sql = @"
UPDATE inv.Inventory
SET IsDeleted = 0
WHERE InventoryId = @InventoryId AND IsDeleted = 1;

SELECT @@ROWCOUNT;
";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });

            var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

            if (rows == 0)
            {
                // Either not found OR already not deleted.
                // Prefer 404 for not found; to distinguish, we can check existence.
                const string existsSql = @"SELECT COUNT(1) FROM inv.Inventory WHERE InventoryId = @InventoryId;";
                using var existsCmd = new SqlCommand(existsSql, conn) { CommandTimeout = 30 };
                existsCmd.Parameters.Add(new SqlParameter("@InventoryId", System.Data.SqlDbType.Int) { Value = id });

                var exists = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(cancellationToken)) > 0;

                if (!exists)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteStringAsync("Item not found.", cancellationToken);
                    return notFound;
                }

                // Already active (not deleted)
                return req.CreateResponse(HttpStatusCode.NoContent);
            }

            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in UndeleteItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in UndeleteItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
