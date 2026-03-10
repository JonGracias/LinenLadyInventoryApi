// /Application/Keywords/GenerateKeywordsHandler.cs
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Application.Keywords;

public record GenerateKeywordsResult(
    int    InventoryId,
    string KeywordsJson,
    bool   VectorRefreshed,
    bool   SeoRefreshed
);

public sealed class GenerateKeywordsHandler
{
    private readonly ILogger<GenerateKeywordsHandler> _logger;
    private readonly GenerateSeoHandler _seoHandler;
    private static readonly HttpClient Http = new();

    public GenerateKeywordsHandler(
        ILogger<GenerateKeywordsHandler> logger,
        GenerateSeoHandler seoHandler)
    {
        _logger     = logger;
        _seoHandler = seoHandler;
    }

    public async Task<GenerateKeywordsResult> HandleAsync(int inventoryId, string? hint, CancellationToken ct)
    {
        if (inventoryId <= 0) throw new ArgumentException("Invalid id.");

        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing SQL_CONNECTION_STRING.");

        // 1. Load item text + admin notes together
        var item = await LoadItemAsync(connStr, inventoryId, ct)
            ?? throw new KeyNotFoundException($"Item {inventoryId} not found.");

        // 2. Build prompt and call Azure OpenAI for keywords
        var keywordsJson = await GenerateKeywordsAsync(item, hint, ct);

        // 3. Upsert keywords into inv.InventoryAiMeta
        await UpsertKeywordsAsync(connStr, inventoryId, item.AdminNotes, keywordsJson, ct);

        // 4. Trigger vector refresh so the new keywords are included in embeddings
        var vectorRefreshed = await RefreshVectorAsync(inventoryId, ct);

        // 5. Generate SEO from the freshly saved keywords
        var seoRefreshed = await RefreshSeoAsync(inventoryId, ct);

        return new GenerateKeywordsResult(inventoryId, keywordsJson, vectorRefreshed, seoRefreshed);
    }

    // ── Data loading ────────────────────────────────────────────────────────

    private record ItemData(
        string  Name,
        string? Description,
        int     UnitPriceCents,
        string? AdminNotes
    );

    private static async Task<ItemData?> LoadItemAsync(
        string connStr, int inventoryId, CancellationToken ct)
    {
        const string sql = """
            SELECT
                i.Name,
                i.Description,
                i.UnitPriceCents,
                m.AdminNotes
            FROM inv.Inventory i
            LEFT JOIN inv.InventoryAiMeta m ON m.InventoryId = i.InventoryId
            WHERE i.InventoryId = @Id AND i.IsDeleted = 0;
            """;

        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@Id", inventoryId);

        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new ItemData(
            Name:           r.GetString(0),
            Description:    r.IsDBNull(1) ? null : r.GetString(1),
            UnitPriceCents: r.GetInt32(2),
            AdminNotes:     r.IsDBNull(3) ? null : r.GetString(3)
        );
    }

    // ── Azure OpenAI ────────────────────────────────────────────────────────

    private async Task<string> GenerateKeywordsAsync(ItemData item, string? hint, CancellationToken ct)
    {
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.TrimEnd('/');
        var apiKey     = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-15-preview";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            throw new InvalidOperationException("Missing Azure OpenAI chat config (AZURE_OPENAI_CHAT_DEPLOYMENT).");

        var prompt = BuildPrompt(item, hint);
        var url    = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";

        var payload = new
        {
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = prompt }
            },
            temperature      = 0.2,
            max_tokens       = 800,
            response_format  = new { type = "json_object" }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("api-key", apiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure OpenAI error {(int)resp.StatusCode}: {body}");

        using var doc    = JsonDocument.Parse(body);
        var content      = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Validate it's real JSON before storing
        try { JsonDocument.Parse(content); }
        catch { content = "{}"; }

        return content;
    }

    private static string BuildPrompt(ItemData item, string? hint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Item name: {item.Name}");
        sb.AppendLine($"Price: ${item.UnitPriceCents / 100.0:F2}");

        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            sb.AppendLine();
            sb.AppendLine($"Description: {item.Description}");
        }

        if (!string.IsNullOrWhiteSpace(item.AdminNotes))
        {
            sb.AppendLine();
            sb.AppendLine($"Additional seller notes (private context, not shown publicly): {item.AdminNotes}");
        }

        if (!string.IsNullOrWhiteSpace(hint))
    {
        sb.AppendLine();
        sb.AppendLine($"IMPORTANT instruction from seller: {hint}");
        sb.AppendLine("You MUST follow this instruction. If it says to exclude something, exclude it from ALL keyword categories.");
    }

        return sb.ToString().Trim();
    }

    private const string SystemPrompt = """
        You are a product cataloguing assistant for an antique and vintage item marketplace.
        
        Given item details, extract structured keywords that will help buyers find this item through search.
        Be specific and thorough — think about what a buyer might type to find this item.
        
        Return ONLY a JSON object. Use only the categories that are relevant — omit categories that don't apply.
        You may invent category names appropriate to the item type.
        
        Example structure (adapt categories to the item):
        {
          "colors": ["blue", "gold", "ivory"],
          "materials": ["linen", "cotton", "brass"],
          "patterns": ["floral", "geometric"],
          "style": ["art deco", "Victorian", "farmhouse"],
          "era": ["1920s", "mid-century"],
          "condition": ["excellent", "minor wear"],
          "use_case": ["dining", "wedding", "decorative", "display"],
          "item_type": ["tablecloth", "candlestick", "vase"],
          "dimensions": ["rectangular", "60x120 inches"],
          "descriptors": ["ornate", "heavy", "patinated", "hand-embroidered"],
          "search_keywords": ["vintage brass candlestick", "art deco table decor"]
        }
        
        The "search_keywords" array should contain 3-8 natural language phrases a buyer might search for.
        """;

    // ── DB upsert ───────────────────────────────────────────────────────────

    private static async Task UpsertKeywordsAsync(
        string connStr, int inventoryId, string? adminNotes,
        string keywordsJson, CancellationToken ct)
    {
        const string sql = """
            MERGE inv.InventoryAiMeta AS t
            USING (SELECT @InventoryId AS InventoryId) AS s
            ON t.InventoryId = s.InventoryId
            WHEN MATCHED THEN
                UPDATE SET
                    KeywordsJson        = @KeywordsJson,
                    KeywordsGeneratedAt = SYSUTCDATETIME(),
                    UpdatedAt           = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (InventoryId, AdminNotes, KeywordsJson, KeywordsGeneratedAt)
                VALUES (@InventoryId, @AdminNotes, @KeywordsJson, SYSUTCDATETIME());
            """;

        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@InventoryId", inventoryId);
        cmd.Parameters.AddWithValue("@AdminNotes",  (object?)adminNotes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@KeywordsJson", keywordsJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Vector refresh (calls the existing AiSearchVectors endpoint) ────────

    private async Task<bool> RefreshVectorAsync(int inventoryId, CancellationToken ct)
    {
        try
        {
            var apiBase = Environment.GetEnvironmentVariable("SELF_BASE_URL")
                       ?? Environment.GetEnvironmentVariable("LINENLADY_API_BASE_URL")
                       ?? "http://localhost:7071";

            var url = $"{apiBase}/api/items/{inventoryId}/vectors/refresh";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { purpose = "item_text", force = true }),
                Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector refresh failed for item {Id}.", inventoryId);
            return false;
        }
    }

    // ── SEO generation ──────────────────────────────────────────────────────

    private async Task<bool> RefreshSeoAsync(int inventoryId, CancellationToken ct)
    {
        try
        {
            await _seoHandler.HandleAsync(inventoryId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SEO generation failed for item {Id} — keywords saved but SEO not updated.", inventoryId);
            return false;
        }
    }
}