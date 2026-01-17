using System.Net;
using LinenLady.Inventory.Functions.Models;
using LinenLady.Inventory.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace LinenLady.Inventory.Functions;

public sealed class GetImageReadUrls
{
    [Function("GetImageReadUrls")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "images/read-urls")]
        HttpRequestData req)
    {
        var input =
            await req.ReadFromJsonAsync<ReadUrlsRequest>()
            ?? new ReadUrlsRequest();

        if (input.Paths is null || input.Paths.Count == 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Body must include { paths: [...] }");
            return bad;
        }

        if (input.Paths.Count > 200)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Max 200 paths per request.");
            return bad;
        }

        var conn = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
        var container = Environment.GetEnvironmentVariable("IMAGE_CONTAINER_NAME");

        if (string.IsNullOrWhiteSpace(conn) || string.IsNullOrWhiteSpace(container))
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Missing blob storage configuration.");
            return err;
        }

        var ttlMinutes = input.TtlMinutes ?? 60;
        if (ttlMinutes < 5) ttlMinutes = 5;
        if (ttlMinutes > 240) ttlMinutes = 240;

        var ttl = TimeSpan.FromMinutes(ttlMinutes);

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in input.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var blobName = path.TrimStart('/');

            urls[blobName] = BlobSas.BuildReadUrl(
                conn,
                container,
                blobName,
                ttl
            );
        }

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new ReadUrlsResponse { Urls = urls });
        return ok;
    }
}
