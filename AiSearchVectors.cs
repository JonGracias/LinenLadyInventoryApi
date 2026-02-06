using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class AiSearchVectors
{
    private readonly ILogger _logger;
    private static readonly HttpClient Http = new HttpClient();

    public AiSearchVectors(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AiSearchVectors>();
    }

    private sealed class RefreshVectorRequest
    {
        public string? purpose { get; set; } = "item_text";
        public bool force { get; set; } = false;
    }

    [Function("AiSearchVectors")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/vectors/refresh")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", ct);
            return bad;
        }

        var sqlConnStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(sqlConnStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        // Azure OpenAI embeddings config
        var aoaiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.TrimEnd('/');
        var aoaiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var embDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_DEPLOYMENT");
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-15-preview";

        if (string.IsNullOrWhiteSpace(aoaiEndpoint) || string.IsNullOrWhiteSpace(aoaiKey) || string.IsNullOrWhiteSpace(embDeployment))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing AZURE_OPENAI_ENDPOINT/API_KEY/EMBEDDINGS_DEPLOYMENT.", ct);
            return bad;
        }

        RefreshVectorRequest body;
        try { body = await req.ReadFromJsonAsync<RefreshVectorRequest>(ct) ?? new RefreshVectorRequest(); }
        catch { body = new RefreshVectorRequest(); }

        var purpose = string.IsNullOrWhiteSpace(body.purpose) ? "item_text" : body.purpose.Trim();
        if (purpose.Length > 50)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("purpose too long (max 50).", ct);
            return bad;
        }

        // 1) Load item text
        var item = await LoadItemText(sqlConnStr, id, ct);
        if (item is null)
        {
            var nf = req.CreateResponse(HttpStatusCode.NotFound);
            await nf.WriteStringAsync("Item not found.", ct);
            return nf;
        }

        // Build the embedding input string (stable formatting)
        var inputText = BuildEmbeddingText(item.Value.Name, item.Value.Description);
        if (string.IsNullOrWhiteSpace(inputText))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Item has no text to embed (name/description empty).", ct);
            return bad;
        }

        // 2) Hash for idempotency
        var hash = Sha256Bytes(inputText);

        // 3) Check existing vector
        var existing = await LoadExistingVector(sqlConnStr, id, purpose, embDeployment!, ct);
        if (!body.force && existing is not null && ByteArrayEqual(existing.Value.ContentHash, hash))
        {
            var okNoChange = req.CreateResponse(HttpStatusCode.OK);
            await okNoChange.WriteAsJsonAsync(new
            {
                inventoryId = id,
                purpose,
                model = embDeployment,
                status = "unchanged",
                dimensions = existing.Value.Dimensions,
                vectorId = existing.Value.VectorId
            }, ct);
            return okNoChange;
        }

        // 4) Create embedding
        var embedding = await CreateEmbeddingAsync(aoaiEndpoint!, aoaiKey!, embDeployment!, apiVersion, inputText, ct);
        var dims = embedding.Length;

        // 5) Upsert
        var vectorJson = JsonSerializer.Serialize(embedding);
        var vectorId = await UpsertVector(sqlConnStr, id, purpose, embDeployment!, dims, hash, vectorJson, ct);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new
        {
            inventoryId = id,
            purpose,
            model = embDeployment,
            status = existing is null ? "created" : "updated",
            dimensions = dims,
            vectorId
        }, ct);
        return ok;
    }

    private static string BuildEmbeddingText(string name, string? description)
    {
        name = (name ?? "").Trim();
        var desc = (description ?? "").Trim();

        if (string.IsNullOrWhiteSpace(desc))
            return name;

        return $"{name}\n\n{desc}";
    }

    private static byte[] Sha256Bytes(string s)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(s));
    }

    private static bool ByteArrayEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static async Task<(string Name, string? Description)?> LoadItemText(string connStr, int id, CancellationToken ct)
    {
        const string sql = @"
SELECT Name, Description
FROM inv.Inventory
WHERE InventoryId = @Id AND IsDeleted = 0;
";
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@Id", id);

        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var name = r.GetString(0);
        var desc = r.IsDBNull(1) ? null : r.GetString(1);
        return (name, desc);
    }

    private static async Task<(int VectorId, int Dimensions, byte[] ContentHash)?> LoadExistingVector(
        string connStr, int inventoryId, string purpose, string model, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) VectorId, Dimensions, ContentHash
FROM inv.InventoryVector
WHERE InventoryId = @InventoryId AND VectorPurpose = @Purpose AND Model = @Model;
";
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@InventoryId", inventoryId);
        cmd.Parameters.AddWithValue("@Purpose", purpose);
        cmd.Parameters.AddWithValue("@Model", model);

        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return (r.GetInt32(0), r.GetInt32(1), (byte[])r.GetValue(2));
    }

    private static async Task<int> UpsertVector(
        string connStr, int inventoryId, string purpose, string model,
        int dimensions, byte[] contentHash, string vectorJson, CancellationToken ct)
    {
        const string sql = @"
MERGE inv.InventoryVector AS t
USING (SELECT @InventoryId AS InventoryId, @Purpose AS VectorPurpose, @Model AS Model) AS s
ON t.InventoryId = s.InventoryId AND t.VectorPurpose = s.VectorPurpose AND t.Model = s.Model
WHEN MATCHED THEN
    UPDATE SET
        Dimensions = @Dimensions,
        ContentHash = @ContentHash,
        VectorJson = @VectorJson,
        UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (InventoryId, VectorPurpose, Model, Dimensions, ContentHash, VectorJson)
    VALUES (@InventoryId, @Purpose, @Model, @Dimensions, @ContentHash, @VectorJson)
OUTPUT inserted.VectorId;
";
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@InventoryId", inventoryId);
        cmd.Parameters.AddWithValue("@Purpose", purpose);
        cmd.Parameters.AddWithValue("@Model", model);
        cmd.Parameters.AddWithValue("@Dimensions", dimensions);
        cmd.Parameters.AddWithValue("@ContentHash", contentHash);
        cmd.Parameters.AddWithValue("@VectorJson", vectorJson);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<float[]> CreateEmbeddingAsync(
        string endpoint, string apiKey, string deployment, string apiVersion, string input, CancellationToken ct)
    {
        // Azure OpenAI embeddings endpoint shape: input can be string or array. :contentReference[oaicite:2]{index=2}
        var url = $"{endpoint}/openai/deployments/{deployment}/embeddings?api-version={apiVersion}";

        var payload = new
        {
            input = input
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Headers.Add("api-key", apiKey);
        httpReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(httpReq, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure OpenAI embeddings error {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var embArray = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");

        var result = new float[embArray.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
            result[i] = embArray[i].GetSingle();

        return result;
    }
}
