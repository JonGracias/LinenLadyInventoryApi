using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using static LinenLady.Inventory.Contracts.CreateItemsContracts;

namespace LinenLady.Inventory.Application.Items;

/// <summary>
/// Creates a draft item (inv.Inventory) and returns SAS upload targets for images.
/// This is the full logic lifted from the original function. :contentReference[oaicite:4]{index=4} :contentReference[oaicite:5]{index=5}
/// </summary>
public sealed class CreateItemsHandler
{
    private readonly ILogger<CreateItemsHandler> _logger;

    public CreateItemsHandler(ILogger<CreateItemsHandler> logger)
    {
        _logger = logger;
    }

    public async Task<CreateItemsResult> HandleAsync(CreateItemsRequest body, CancellationToken ct)
    {
        // -----------------------------
        // 1) Read environment config
        // -----------------------------
        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("Server misconfigured: missing SQL_CONNECTION_STRING.");

        var storageConn =
            Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING") ??
            Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        if (string.IsNullOrWhiteSpace(storageConn))
            throw new InvalidOperationException("Server misconfigured: missing storage connection string.");

        var containerName =
            Environment.GetEnvironmentVariable("IMAGE_CONTAINER_NAME") ??
            "inventory-images";

        // -----------------------------
        // 2) Validate request + clamp
        // -----------------------------
        var files = body.Files;
        int requestedCount = (files is { Count: > 0 }) ? files.Count : (body.Count ?? 0);
        if (requestedCount <= 0)
            throw new ArgumentException("Provide files[] or count > 0.");

        int count = Math.Clamp(requestedCount, 1, 4);

        // -----------------------------
        // 3) Generate identifiers
        // -----------------------------
        var publicId = Guid.NewGuid();
        var publicIdN = publicId.ToString("N");
        var sku = $"DRAFT-{publicIdN}";

        var name = string.IsNullOrWhiteSpace(body.TitleHint)
            ? "Draft"
            : body.TitleHint!.Trim();

        var description = string.IsNullOrWhiteSpace(body.Notes)
            ? null
            : body.Notes!.Trim();

        // -----------------------------
        // 4) Insert draft into SQL
        // -----------------------------
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

            cmd.Parameters.Add(new SqlParameter("@Sku", System.Data.SqlDbType.NVarChar, 64)
            { Value = sku });

            cmd.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 255)
            { Value = name });

            cmd.Parameters.Add(new SqlParameter("@Description", System.Data.SqlDbType.NVarChar, -1)
            { Value = (object?)description ?? DBNull.Value });

            inventoryId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error creating draft item.");
            throw new InvalidOperationException("Database error.");
        }

        // -----------------------------
        // 5) Generate SAS upload URLs
        // -----------------------------
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

                // same naming scheme as original :contentReference[oaicite:6]{index=6}
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

            return new CreateItemsResult(
                InventoryId: inventoryId,
                PublicId: publicIdN,
                Sku: sku,
                Container: containerName,
                ExpiresOnUtc: expiresOn.UtcDateTime,
                Uploads: targets
            );
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "SAS generation failed (likely missing AccountKey).");
            throw new InvalidOperationException("SAS generation failed. Ensure storage connection string includes AccountKey.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error creating draft uploads.");
            throw new InvalidOperationException("Server error.");
        }
    }

    // same helpers as original :contentReference[oaicite:7]{index=7}
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
