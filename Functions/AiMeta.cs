// /Functions/AiMeta.cs
// Three related Azure Functions for AI metadata management

using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Application.Keywords;
using System.Text.Json.Serialization;

namespace LinenLady.Inventory.Functions;

// ── POST /api/items/{id}/keywords/generate ─────────────────────────────────

public sealed class GenerateKeywords
{
    private readonly GenerateKeywordsHandler _handler;

    public GenerateKeywords(GenerateKeywordsHandler handler)
    {
        _handler = handler;
    }

    [Function("GenerateKeywords")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/keywords/generate")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(id, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (KeyNotFoundException ex)
        {
            var nf = req.CreateResponse(HttpStatusCode.NotFound);
            await nf.WriteStringAsync(ex.Message, ct);
            return nf;
        }
        catch (ArgumentException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, ct);
            return bad;
        }
        catch (InvalidOperationException ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message, ct);
            return err;
        }
    }
}

// ── GET /api/items/{id}/ai-meta ────────────────────────────────────────────

public sealed class GetAiMeta
{
    private readonly ILogger _logger;

    public GetAiMeta(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GetAiMeta>();
    }

    [Function("GetAiMeta")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}/ai-meta")]
        HttpRequestData req,
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
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Missing SQL_CONNECTION_STRING.", ct);
            return err;
        }

        const string sql = """
            SELECT m.AdminNotes, m.KeywordsJson, m.KeywordsGeneratedAt,
                   m.SeoJson, m.SeoGeneratedAt, m.UpdatedAt
            FROM inv.InventoryAiMeta m
            WHERE m.InventoryId = @Id;
            """;

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@Id", id);

            using var r = await cmd.ExecuteReaderAsync(ct);

            object result;
            if (await r.ReadAsync(ct))
            {
                result = new
                {
                    AdminNotes          = r.IsDBNull(0) ? null : r.GetString(0),
                    KeywordsJson        = r.IsDBNull(1) ? null : r.GetString(1),
                    KeywordsGeneratedAt = r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2),
                    SeoJson             = r.IsDBNull(3) ? null : r.GetString(3),
                    SeoGeneratedAt      = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4),
                    UpdatedAt           = r.GetDateTime(5),
                };
            }
            else
            {
                // No meta row yet — return empty scaffold
                result = new
                {
                    AdminNotes          = (string?)null,
                    KeywordsJson        = (string?)null,
                    KeywordsGeneratedAt = (DateTime?)null,
                    SeoJson             = (string?)null,
                    SeoGeneratedAt      = (DateTime?)null,
                    UpdatedAt           = (DateTime?)null,
                };
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in GetAiMeta.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }
    }
}

// ── PATCH /api/items/{id}/ai-meta/notes ────────────────────────────────────

public sealed class UpsertAdminNotes
{
    private readonly ILogger _logger;

    public UpsertAdminNotes(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<UpsertAdminNotes>();
    }

    [Function("UpsertAdminNotes")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "items/{id:int}/ai-meta/notes")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", ct);
            return bad;
        }

        string? adminNotes;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            adminNotes = doc.RootElement.TryGetProperty("adminNotes", out var prop)
                ? prop.GetString()
                : null;
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Missing SQL_CONNECTION_STRING.", ct);
            return err;
        }

        const string sql = """
            MERGE inv.InventoryAiMeta AS t
            USING (SELECT @InventoryId AS InventoryId) AS s
            ON t.InventoryId = s.InventoryId
            WHEN MATCHED THEN
                UPDATE SET AdminNotes = @AdminNotes, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (InventoryId, AdminNotes)
                VALUES (@InventoryId, @AdminNotes);
            """;

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@InventoryId", id);
            cmd.Parameters.AddWithValue("@AdminNotes", (object?)adminNotes ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(new { ok = true }, ct);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in UpsertAdminNotes.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }
    }
}

// ── POST /api/items/{id}/seo/generate ──────────────────────────────────────

public sealed class GenerateSeo
{
    private readonly GenerateSeoHandler _handler;

    public GenerateSeo(GenerateSeoHandler handler)
    {
        _handler = handler;
    }

    [Function("GenerateSeo")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/seo/generate")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(id, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (KeyNotFoundException ex)
        {
            var nf = req.CreateResponse(HttpStatusCode.NotFound);
            await nf.WriteStringAsync(ex.Message, ct);
            return nf;
        }
        catch (InvalidOperationException ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message, ct);
            return err;
        }
    }
}