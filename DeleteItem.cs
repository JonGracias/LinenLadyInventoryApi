// DeleteItem.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class DeleteItem
{
    private readonly ILogger _logger;

    public DeleteItem(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<DeleteItem>();
    }

    [Function("DeleteItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "items/{id:int}")] HttpRequestData req,
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

        // Soft delete: requires an IsDeleted bit column (or similar).
        // If you don't have it yet, add it and default it to 0.
        const string sql = @"
UPDATE inv.Inventory
SET IsDeleted = 1
WHERE InventoryId = @InventoryId AND IsDeleted = 0;

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
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("Item not found.", cancellationToken);
                return notFound;
            }

            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in DeleteItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in DeleteItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
