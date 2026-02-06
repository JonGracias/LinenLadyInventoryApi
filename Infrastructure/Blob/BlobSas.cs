using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace LinenLady.Inventory.Functions.Infrastructure.Blob;

public static class BlobSas
{
    public static string BuildReadUrl(
        string connectionString,
        string containerName,
        string blobName,
        TimeSpan ttl)
    {
        var container = new BlobContainerClient(connectionString, containerName);
        var blobClient = container.GetBlobClient(blobName);

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var accountName = parts.First(p => p.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase))
            .Split('=', 2)[1];
        var accountKey = parts.First(p => p.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase))
            .Split('=', 2)[1];

        var cred = new StorageSharedKeyCredential(accountName, accountKey);

        var sas = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresOn = DateTimeOffset.UtcNow.Add(ttl),
        };

        sas.SetPermissions(BlobSasPermissions.Read);

        var qs = sas.ToSasQueryParameters(cred).ToString();
        return $"{blobClient.Uri}?{qs}";
    }
}
