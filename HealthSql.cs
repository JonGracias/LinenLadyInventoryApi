using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public class GetHealth
{
    private readonly ILogger _logger;

    public GetHealth(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GetHealth>();
    }

    [Function("GetHealth")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");

        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var cmd = new SqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("SQL OK");

        return response;
    }
}
