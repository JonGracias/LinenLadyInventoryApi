using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class GetItemCounts
{
    private readonly ILogger _logger;

    public GetItemCounts(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GetItemCounts>();
    }

    [Function("GetItemCounts")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/counts")] HttpRequestData req,
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
        allCount =
            (SELECT COUNT_BIG(1) FROM inv.Inventory WHERE IsDeleted = 0),
        draftsCount =
            (SELECT COUNT_BIG(1) FROM inv.Inventory WHERE IsDeleted = 0 AND IsDraft = 1),
        publishedCount =
            (SELECT COUNT_BIG(1) FROM inv.Inventory WHERE IsDeleted = 0 AND IsDraft = 0 AND IsActive = 1);";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            long all = 0, drafts = 0, published = 0;

            if (await reader.ReadAsync(cancellationToken))
            {
                all = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0));
                drafts = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                published = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));

            }

            var payload = new
            {
                all,
                drafts,
                published
            };

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(payload, cancellationToken);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in GetItemCounts.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GetItemCounts.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
