// AiPrefillItem.cs
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using LinenLady.Inventory.Functions.Contracts;

namespace LinenLady.Inventory.Functions;

public sealed class AiPrefillItem
{
    private readonly ILogger _logger;

    // Reuse a single HttpClient instance
    private static readonly HttpClient Http = new HttpClient();

    public AiPrefillItem(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AiPrefillItem>();
    }

    private enum PrefillMode
    {
        All,
        Title,
        Description,
        Price
    }

    private sealed class AiPrefillRequest
    {
        public bool Overwrite { get; set; } = false;

        // Existing behavior: cap images used for vision
        public int MaxImages { get; set; } = 4;

        // NEW: optional explicit selection of which InventoryImage.ImageId values to analyze
        public int[]? ImageIds { get; set; }

        // Optional extra context for the model
        public string? TitleHint { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class AiPrefillResult
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int? UnitPriceCents { get; set; }
    }

    // Existing "all fields" endpoint (keep as your default)
    [Function("AiPrefillItem")]
    public Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/ai-prefill")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
        => RunInternal(req, id, PrefillMode.All, cancellationToken);

    // New: title-only
    [Function("AiPrefillItemTitle")]
    public Task<HttpResponseData> RunTitle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/title/ai-prefill")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
        => RunInternal(req, id, PrefillMode.Title, cancellationToken);

    // New: description-only
    [Function("AiPrefillItemDescription")]
    public Task<HttpResponseData> RunDescription(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/description/ai-prefill")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
        => RunInternal(req, id, PrefillMode.Description, cancellationToken);

    // New: price-only
    [Function("AiPrefillItemPrice")]
    public Task<HttpResponseData> RunPrice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/price/ai-prefill")] HttpRequestData req,
        int id,
        CancellationToken cancellationToken)
        => RunInternal(req, id, PrefillMode.Price, cancellationToken);

    private async Task<HttpResponseData> RunInternal(
        HttpRequestData req,
        int id,
        PrefillMode mode,
        CancellationToken cancellationToken)
    {
        var sqlConnStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(sqlConnStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", cancellationToken);
            return bad;
        }

        var blobConnStr = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
        var containerName = Environment.GetEnvironmentVariable("IMAGE_CONTAINER_NAME") ?? "inventory-images";
        if (string.IsNullOrWhiteSpace(blobConnStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing BLOB_STORAGE_CONNECTION_STRING.", cancellationToken);
            return bad;
        }

        AiPrefillRequest body;
        try
        {
            body = await req.ReadFromJsonAsync<AiPrefillRequest>(cancellationToken) ?? new AiPrefillRequest();
        }
        catch
        {
            body = new AiPrefillRequest();
        }

        body.MaxImages = Math.Clamp(body.MaxImages, 1, 8);

        try
        {
            // 0) Load PublicId (source of truth for blob prefix validation)
            var publicId = await LoadPublicId(sqlConnStr, id, cancellationToken);
            if (publicId is null)
            {
                var nf = req.CreateResponse(HttpStatusCode.NotFound);
                await nf.WriteStringAsync("Item not found (missing PublicId).", cancellationToken);
                return nf;
            }

            // 1) Load item + images
            InventoryItemDto? item = await LoadItem(sqlConnStr, id, cancellationToken);
            if (item is null)
            {
                var nf = req.CreateResponse(HttpStatusCode.NotFound);
                await nf.WriteStringAsync("Item not found.", cancellationToken);
                return nf;
            }

            if (item.Images.Count == 0)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Item has no images to analyze.", cancellationToken);
                return bad;
            }

            // 2) Determine which images to use:
            //    - If imageIds provided: use those images (caller order, dedupe), respecting MaxImages
            //    - Else: first N by SortOrder (existing behavior)
            var selectedImages = SelectImages(item, body.ImageIds, body.MaxImages);

            if (selectedImages.Count == 0)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("No valid images selected for analysis.", cancellationToken);
                return bad;
            }

            // 3) Build READ SAS URLs for images
            var blobService = new BlobServiceClient(blobConnStr);
            var container = blobService.GetBlobContainerClient(containerName);

            var imageSasUrls = selectedImages
                .Select(img => ToBlobName(img.ImagePath, containerName, publicId.Value))
                .Where(blobName => !string.IsNullOrWhiteSpace(blobName))
                .Select(blobName => MakeReadSas(container, blobName!, TimeSpan.FromMinutes(15)))
                .ToList();

            if (imageSasUrls.Count == 0)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync(
                    $"Could not resolve valid blob names for images. Expected prefix: images/{publicId.Value:N}/",
                    cancellationToken);
                return bad;
            }

            // 4) Call Azure OpenAI vision to get name/description/price
            var ai = await CallAzureOpenAi(imageSasUrls, body.TitleHint, body.Notes, cancellationToken);

            // 5) Decide what to overwrite (selected fields only)
            var overwrite = body.Overwrite;

            var newName = item.Name;
            var newDesc = item.Description;
            var newPrice = item.UnitPriceCents;

            if (mode is PrefillMode.All or PrefillMode.Title)
            {
                newName = PickString(
                    overwrite: overwrite,
                    current: item.Name,
                    proposed: ai.Name,
                    isPlaceholder: s => string.IsNullOrWhiteSpace(s) || s.Equals("Draft", StringComparison.OrdinalIgnoreCase));
            }

            if (mode is PrefillMode.All or PrefillMode.Description)
            {
                newDesc = PickNullableString(
                    overwrite: overwrite,
                    current: item.Description,
                    proposed: ai.Description,
                    isPlaceholder: s => string.IsNullOrWhiteSpace(s));
            }

            if (mode is PrefillMode.All or PrefillMode.Price)
            {
                newPrice = PickInt(
                    overwrite: overwrite,
                    current: item.UnitPriceCents,
                    proposed: SanitizePrice(ai.UnitPriceCents),
                    isPlaceholder: p => p <= 0);
            }

            // If nothing changed, return current item
            if (newName == item.Name && newDesc == item.Description && newPrice == item.UnitPriceCents)
            {
                var okNoChange = req.CreateResponse(HttpStatusCode.OK);
                await okNoChange.WriteAsJsonAsync(item, cancellationToken);
                return okNoChange;
            }

            // 6) Update DB (only touched columns)
            await UpdateItemPartial(sqlConnStr, id, mode, newName, newDesc, newPrice, cancellationToken);

            // 7) Re-load and return updated item
            var updated = await LoadItem(sqlConnStr, id, cancellationToken);

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(updated, cancellationToken);
            return ok;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in AiPrefillItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", cancellationToken);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in AiPrefillItem.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", cancellationToken);
            return err;
        }
    }

    private static List<InventoryImageDto> SelectImages(
        InventoryItemDto item,
        int[]? imageIds,
        int maxImages)
    {
        // Explicit selection path
        if (imageIds is { Length: > 0 })
        {
            // Only allow ids that belong to this item (item.Images is already scoped by InventoryId)
            var byId = item.Images.ToDictionary(i => i.ImageId, i => i);

            var picked = new List<InventoryImageDto>(capacity: Math.Min(maxImages, imageIds.Length));
            var seen = new HashSet<int>();

            foreach (var id in imageIds)
            {
                if (picked.Count >= maxImages) break;
                if (!seen.Add(id)) continue;
                if (byId.TryGetValue(id, out var img))
                    picked.Add(img);
            }

            // If user passed ids but none matched, fall through to fallback behavior
            if (picked.Count > 0)
                return picked;
        }

        // Fallback path: first N by SortOrder (existing behavior)
        return item.Images
            .OrderBy(i => i.SortOrder)
            .Take(maxImages)
            .ToList();
    }

    private static async Task<Guid?> LoadPublicId(string sqlConnStr, int id, CancellationToken ct)
    {
        const string sql = @"
SELECT i.PublicId
FROM inv.Inventory i
WHERE i.InventoryId = @Id;
";
        using var conn = new SqlConnection(sqlConnStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@Id", id);

        var val = await cmd.ExecuteScalarAsync(ct);
        if (val is null || val is DBNull) return null;

        return (Guid)val;
    }

    private static async Task<InventoryItemDto?> LoadItem(string sqlConnStr, int id, CancellationToken ct)
    {
        const string sql = @"
SELECT
    i.InventoryId,
    i.Sku,
    i.Name,
    i.Description,
    i.QuantityOnHand,
    i.UnitPriceCents,
    img.ImageId,
    img.ImagePath,
    img.IsPrimary,
    img.SortOrder
FROM inv.Inventory i
LEFT JOIN inv.InventoryImage img ON img.InventoryId = i.InventoryId
WHERE i.InventoryId = @Id
ORDER BY i.InventoryId, img.SortOrder;
";

        using var conn = new SqlConnection(sqlConnStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@Id", id);

        using var reader = await cmd.ExecuteReaderAsync(ct);

        InventoryItemDto? item = null;

        const int O_InventoryId = 0;
        const int O_Sku = 1;
        const int O_Name = 2;
        const int O_Description = 3;
        const int O_QuantityOnHand = 4;
        const int O_UnitPriceCents = 5;

        const int O_ImageId = 6;
        const int O_ImagePath = 7;
        const int O_IsPrimary = 8;
        const int O_SortOrder = 9;

        while (await reader.ReadAsync(ct))
        {
            if (item is null)
            {
                item = new InventoryItemDto
                {
                    InventoryId = reader.GetInt32(O_InventoryId),
                    Sku = reader.GetString(O_Sku),
                    Name = reader.GetString(O_Name),
                    Description = reader.IsDBNull(O_Description) ? null : reader.GetString(O_Description),
                    QuantityOnHand = reader.GetInt32(O_QuantityOnHand),
                    UnitPriceCents = reader.GetInt32(O_UnitPriceCents),
                };
            }

            if (!reader.IsDBNull(O_ImageId))
            {
                item.Images.Add(new InventoryImageDto
                {
                    ImageId = reader.GetInt32(O_ImageId),
                    ImagePath = reader.GetString(O_ImagePath),
                    IsPrimary = reader.GetBoolean(O_IsPrimary),
                    SortOrder = reader.GetInt32(O_SortOrder),
                });
            }
        }

        return item;
    }

    private static async Task UpdateItemPartial(
        string sqlConnStr,
        int id,
        PrefillMode mode,
        string name,
        string? description,
        int unitPriceCents,
        CancellationToken ct)
    {
        string sql = mode switch
        {
            PrefillMode.Title => @"
UPDATE inv.Inventory
SET Name = @Name
WHERE InventoryId = @Id;
",
            PrefillMode.Description => @"
UPDATE inv.Inventory
SET Description = @Description
WHERE InventoryId = @Id;
",
            PrefillMode.Price => @"
UPDATE inv.Inventory
SET UnitPriceCents = @UnitPriceCents
WHERE InventoryId = @Id;
",
            _ => @"
UPDATE inv.Inventory
SET Name = @Name,
    Description = @Description,
    UnitPriceCents = @UnitPriceCents
WHERE InventoryId = @Id;
"
        };

        using var conn = new SqlConnection(sqlConnStr);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@Id", id);

        if (mode is PrefillMode.All or PrefillMode.Title)
            cmd.Parameters.AddWithValue("@Name", name);

        if (mode is PrefillMode.All or PrefillMode.Description)
            cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);

        if (mode is PrefillMode.All or PrefillMode.Price)
            cmd.Parameters.AddWithValue("@UnitPriceCents", unitPriceCents);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Uri MakeReadSas(BlobContainerClient container, string blobName, TimeSpan ttl)
    {
        var blob = container.GetBlobClient(blobName);

        // Requires blob client created with account key creds (connection string)
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(ttl),
        };
        sas.SetPermissions(BlobSasPermissions.Read);

        return blob.GenerateSasUri(sas);
    }

    // ImagePath is source-of-truth blob name (preferred), optionally can be a full blob URL.
    // Enforce: images/{PublicId:N}/...
    private static string? ToBlobName(string imagePath, string containerName, Guid publicId)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;

        string candidate;

        // If URL was accidentally stored, extract blob name from it.
        if (Uri.TryCreate(imagePath, UriKind.Absolute, out var uri))
        {
            // uri.AbsolutePath: "/<container>/<blobName>"
            candidate = uri.AbsolutePath.Trim('/');
            var parts = candidate.Split('/', 2);
            if (parts.Length == 2 && parts[0].Equals(containerName, StringComparison.OrdinalIgnoreCase))
                candidate = Uri.UnescapeDataString(parts[1]);
            else
                candidate = Uri.UnescapeDataString(candidate);
        }
        else
        {
            candidate = imagePath.TrimStart('/');
        }

        // Strip querystring if someone stored a SAS URL string (defensive)
        var q = candidate.IndexOf('?');
        if (q >= 0) candidate = candidate[..q];

        candidate = candidate.Trim();

        var requiredPrefix = $"images/{publicId:N}/";
        if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return candidate;
    }

    private static int? SanitizePrice(int? cents)
    {
        if (cents is null) return null;
        var v = cents.Value;

        if (v < 0) v = 0;
        if (v > 500_000) v = 500_000; // $5,000.00
        return v;
    }

    private static string PickString(bool overwrite, string current, string? proposed, Func<string, bool> isPlaceholder)
    {
        if (overwrite)
            return !string.IsNullOrWhiteSpace(proposed) ? proposed.Trim() : current;

        if (isPlaceholder(current) && !string.IsNullOrWhiteSpace(proposed))
            return proposed.Trim();

        return current;
    }

    private static string? PickNullableString(bool overwrite, string? current, string? proposed, Func<string?, bool> isPlaceholder)
    {
        if (overwrite)
            return !string.IsNullOrWhiteSpace(proposed) ? proposed.Trim() : current;

        if (isPlaceholder(current) && !string.IsNullOrWhiteSpace(proposed))
            return proposed.Trim();

        return current;
    }

    private static int PickInt(bool overwrite, int current, int? proposed, Func<int, bool> isPlaceholder)
    {
        if (overwrite)
            return proposed ?? current;

        if (isPlaceholder(current) && proposed.HasValue)
            return proposed.Value;

        return current;
    }

    private static async Task<AiPrefillResult> CallAzureOpenAi(
        IReadOnlyList<Uri> imageUrls,
        string? titleHint,
        string? notes,
        CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.TrimEnd('/');
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-15-preview";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            throw new InvalidOperationException("Missing Azure OpenAI env vars (AZURE_OPENAI_ENDPOINT/API_KEY/DEPLOYMENT).");

        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";

        // Base instruction text
        var sb = new StringBuilder();
        sb.AppendLine("Analyze the item photos and return ONLY valid JSON (no markdown).");
        sb.AppendLine("Schema:");
        sb.AppendLine("{");
        sb.AppendLine(@"  ""name"": string,");
        sb.AppendLine(@"  ""description"": string,");
        sb.AppendLine(@"  ""unitPriceCents"": number");
        sb.AppendLine("}");
        sb.AppendLine("Rules:");
        sb.AppendLine("- name: short, product-style (no quotes)");
        sb.AppendLine("- description: 1-2 sentences, factual, avoid hype");
        sb.AppendLine("- unitPriceCents: integer cents (USD), reasonable resale price");

        // Optional context
        if (!string.IsNullOrWhiteSpace(titleHint))
        {
            sb.AppendLine();
            sb.AppendLine("Title hint (optional; may be wrong):");
            sb.AppendLine(titleHint.Trim());
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.AppendLine();
            sb.AppendLine("Notes (optional; may be partial):");
            sb.AppendLine(notes.Trim());
        }

        // Build "content": [ {type:text}, {type:image_url}, ... ]
        var content = new List<object>
        {
            new
            {
                type = "text",
                text = sb.ToString()
            }
        };

        foreach (var u in imageUrls)
            content.Add(new { type = "image_url", image_url = new { url = u.ToString() } });

        var payload = new
        {
            messages = new object[]
            {
                new { role = "user", content }
            },
            temperature = 0.2,
            max_tokens = 400
        };

        var json = JsonSerializer.Serialize(payload);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Headers.Add("api-key", apiKey);
        httpReq.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(httpReq, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure OpenAI error {(int)resp.StatusCode}: {respBody}");

        using var doc = JsonDocument.Parse(respBody);
        var contentText = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        var clean = ExtractFirstJsonObject(contentText);

        return JsonSerializer.Deserialize<AiPrefillResult>(clean, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AiPrefillResult();
    }


    private static string ExtractFirstJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start >= 0 && end > start)
            return s.Substring(start, end - start + 1);

        return "{}";
    }
}
