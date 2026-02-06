using System.Net;
using LinenLady.Inventory.Application.Images;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions.Functions;

public sealed class GetItemImages
{
    private readonly ILogger _logger;
    private readonly GetImagesHandler _handler;

    public GetItemImages(ILoggerFactory loggerFactory, GetImagesHandler handler)
    {
        _logger = loggerFactory.CreateLogger<GetItemImages>();
        _handler = handler;
    }

    [Function("GetItemImages")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}/images")]
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

        // Optional: allow ?ttlMinutes=60
        int? ttlMinutes = null;
        if (int.TryParse(System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("ttlMinutes"), out var parsed))
            ttlMinutes = parsed;

        var blobConn = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
        var container = Environment.GetEnvironmentVariable("IMAGE_CONTAINER_NAME");

        if (string.IsNullOrWhiteSpace(blobConn) || string.IsNullOrWhiteSpace(container))
        {
            _logger.LogError("Missing blob storage configuration (BLOB_STORAGE_CONNECTION_STRING / IMAGE_CONTAINER_NAME).");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Missing blob storage configuration.", ct);
            return err;
        }

        var (exists, images) = await _handler.Handle(id, ttlMinutes, blobConn, container, ct);

        if (!exists)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Item not found.", ct);
            return notFound;
        }

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(images, ct);
        return ok;
    }
}
