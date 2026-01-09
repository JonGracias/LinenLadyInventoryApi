using System.Net;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class CreateDraftItemUploads
{
    private readonly ILogger _logger;

    public CreateDraftItemUploads(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CreateDraftItemUploads>();
    }

    public sealed record FileSpec(
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("contentType")] string? ContentType
    );

    public sealed record CreateDraftRequest(
        [property: JsonPropertyName("titleHint")] string? TitleHint,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("count")] int? Count,
        [property: JsonPropertyName("files")] List<FileSpec>? Files
    );

    public sealed record UploadTarget(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("blobName")] string BlobName,
        [property: JsonPropertyName("uploadUrl")] string UploadUrl,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("requiredHeaders")] Dictionary<string, string> RequiredHeaders,
        [property: JsonPropertyName("contentType")] string ContentType
    );

    [Function("CreateDraftItemUploads")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/drafts")] HttpRequestData req,
        CancellationToken ct)
    {
        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        var storageConn =
            Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING") ??
            Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        if (string.IsNullOrWhiteSpace(storageConn))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing storage connection string.", ct);
            return bad;
        }

        var containerName =
            Environment.GetEnvironmentVariable("IMAGE_CONTAINER_NAME") ??
            "inventory-images";

        CreateDraftRequest? body;
        try { body = await req.ReadFromJsonAsync<CreateDraftRequest>(ct); }
        catch { body = null; }

        var files = body?.Files;

        // If files[] provided, it is the source of truth for count. Otherwise use count.
        int requestedCount = (files is { Count: > 0 }) ? files.Count : (body?.Count ?? 0);
        if (requestedCount <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Provide files[] or count > 0.", ct);
            return bad;
        }

        // Option B limit: 1..4 images per draft creation
        int count = Math.Clamp(requestedCount, 1, 4);

        // Draft basics
        var publicId = Guid.NewGuid();                 // NO DEFAULT in DB; must be supplied
        var publicIdN = publicId.ToString("N");        // blob folder format: no hyphens
        var sku = $"DRAFT-{publicIdN}";                // <= 64 chars
        var name = string.IsNullOrWhiteSpace(body?.TitleHint) ? "Draft" : body!.TitleHint!.Trim();

        // You don't have a Notes column; store notes in Description as a temporary hint.
        var description = string.IsNullOrWhiteSpace(body?.Notes) ? null : body!.Notes!.Trim();

        // Insert only columns that are NOT safely defaultable:
        // - PublicId (no default by design)
        // - Sku (unique; cannot be safely defaulted)
        // - Name (required)
        // - Description (optional)
        //
        // Everything else should come from DB defaults.
        const string insertSql = @"
INSERT INTO inv.Inventory
(
  PublicId,
  Sku,
  Name,
  Description
)
OUTPUT INSERTED.InventoryId
VALUES
(
  @PublicId,
  @Sku,
  @Name,
  @Description
);";

        int inventoryId;

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(insertSql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@PublicId", publicId);
            cmd.Parameters.Add(new SqlParameter("@Sku", System.Data.SqlDbType.NVarChar, 64) { Value = sku });
            cmd.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 255) { Value = name });
            cmd.Parameters.Add(new SqlParameter("@Description", System.Data.SqlDbType.NVarChar, -1)
            {
                Value = (object?)description ?? DBNull.Value
            });

            inventoryId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error creating draft item.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }

        // Generate SAS upload URLs for final blob paths
        var expiresInMinutes = 15;
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(expiresInMinutes);

        try
        {
            var service = new BlobServiceClient(storageConn);
            var container = service.GetBlobContainerClient(containerName);
            await container.CreateIfNotExistsAsync(cancellationToken: ct);

            var targets = new List<UploadTarget>(count);

            for (int i = 0; i < count; i++)
            {
                FileSpec? spec = null;
                if (files is { Count: > 0 } && i < files.Count)
                    spec = files[i];

                var ext = NormalizeExtension(spec?.FileName) ?? ".jpg";
                var contentType = NormalizeContentType(spec?.ContentType, ext);

                // blobName format: images/{PublicId:N}/{index}-{guid}{ext}
                var blobName = $"images/{publicIdN}/{i + 1:00}-{Guid.NewGuid():N}{ext}";
                var blobClient = container.GetBlobClient(blobName);

                var sas = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b",
                    ExpiresOn = expiresOn
                };
                sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

                var uploadUri = blobClient.GenerateSasUri(sas);

                targets.Add(new UploadTarget(
                    Index: i + 1,
                    BlobName: blobName,
                    UploadUrl: uploadUri.ToString(),
                    Method: "PUT",
                    RequiredHeaders: new Dictionary<string, string>
                    {
                        ["x-ms-blob-type"] = "BlockBlob",
                        ["Content-Type"] = contentType
                    },
                    ContentType: contentType
                ));
            }

            var payload = new
            {
                inventoryId,
                publicId = publicIdN,   // return N since it’s your folder naming convention
                sku,
                container = containerName,
                expiresOnUtc = expiresOn.UtcDateTime,
                uploads = targets
            };

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(payload, ct);
            return ok;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "SAS generation failed (likely missing AccountKey).");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("SAS generation failed. Ensure storage connection string includes AccountKey.", ct);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error creating draft uploads.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", ct);
            return err;
        }
    }

    private static string? NormalizeExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var ext = Path.GetExtension(fileName.Trim());
        if (string.IsNullOrWhiteSpace(ext)) return null;

        ext = ext.ToLowerInvariant();

        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" => ext,
            _ => ".jpg"
        };
    }

    private static string NormalizeContentType(string? contentType, string ext)
    {
        if (!string.IsNullOrWhiteSpace(contentType)) return contentType.Trim();

        return ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            _ => "image/jpeg"
        };
    }
}
