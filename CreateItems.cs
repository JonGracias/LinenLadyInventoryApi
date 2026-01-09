// CreateItem.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Models;

namespace LinenLady.Inventory.Functions;

public sealed class CreateItem
{
    private readonly ILogger _logger;

    public CreateItem(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CreateItem>();
    }

    public sealed record CreateItemRequest(
        string Sku,
        string Name,
        string? Description,
        int QuantityOnHand,
        int UnitPriceCents
    );

    [Function("CreateItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items")] HttpRequestData req,
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

        CreateItemRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<CreateItemRequest>(cancellationToken);
        }
        catch
        {
            body = null;
        }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", cancellationToken);
            return bad;
        }

        if (string.IsNullOrWhiteSpace(body.Sku) || string.IsNullOrWhiteSpace(body.Name))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Sku and Name are required.", cancellationToken);
            return bad;
        }

        if (body.QuantityOnHand < 0 || body.UnitPriceCents < 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("QuantityOnHand and UnitPriceCents must be >= 0.", cancellationToken);
            return bad;
        }

        const string sql = @"
            INSERT INTO inv.Inventory (Sku, Name, Description, QuantityOnHand, UnitPriceCents)
            OUTPUT INSERTED.InventoryId
            VALUES (@Sku, @Name, @Description, @QuantityOnHand, @UnitPriceCents);
            ";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };

            cmd.Parameters.Add(new SqlParameter("@Sku", System.Data.SqlDbType.NVarChar, 64) { Value = body.Sku.Trim() });
            cmd.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 256) { Value = body.Name.Trim() });
            cmd.Parameters.Add(new SqlParameter("@Description", System.Data.SqlDbType.NVarChar, -1)
            {
                Value = (object?)body.Description?.Trim() ?? DBNull.Value
            });
            cmd.Parameters.Add(new SqlParameter("@QuantityOnHand", System.Data.SqlDbType.Int) { Value = body.QuantityOnHand });
            cmd.Parameters.Add(new SqlParameter("@UnitPriceCents", System.Data.SqlDbType.Int) { Value = body.UnitPriceCents });

            var newIdObj = await cmd.ExecuteScalarAsync(cancellationToken);
            var newId = Convert.ToInt32(newIdObj);

            // Return created item (images empty)
            var createdItem = new InventoryItemDto
            {
                InventoryId = newId,
                Sku = body.Sku.Trim(),
                Name = body.Name.Trim(),
                Description = body.Description?.Trim(),
                QuantityOnHand = body.QuantityOnHand,
                UnitPriceCents = body.UnitPriceCents,
            };

            var created = req.CreateResponse(HttpStatusCode.Created);
            created.Headers.Add("Location", $"/api/items/{newId}");
            await created.WriteAsJsonAsync(createdItem, cancellationToken);
            return created;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601) // unique constraint / duplicate key
        {
            _logger.LogWarning(ex, "Duplicate key creating item.");
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteStringAsync("An item with the same unique key already exists.", cancellationToken);
            return conflict;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in CreateItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in CreateItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }
}
