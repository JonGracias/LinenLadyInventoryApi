// /Functions/ImageFunctions.cs
// Azure Function entry points for:
//   GET  /api/items/{id}/images/new-blob-url   → NewBlobUrlFunction
//   DELETE /api/items/{id}/images/{imageId}    → DeleteImageFunction

using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Images;

namespace LinenLady.Inventory.Functions;

public sealed class ImageFunctions
{
    private readonly NewBlobUrlHandler    _newBlobUrl;
    private readonly DeleteImageHandler   _deleteImage;

    public ImageFunctions(
        NewBlobUrlHandler  newBlobUrl,
        DeleteImageHandler deleteImage)
    {
        _newBlobUrl  = newBlobUrl;
        _deleteImage = deleteImage;
    }

    // ── GET /api/items/{id}/images/new-blob-url ──────────────────────────────
    // Query params: fileName (required), contentType (optional)
    // Returns: { UploadUrl, RequiredHeaders, ContentType, BlobName }

    [Function("GetNewBlobUrl")]
    public async Task<HttpResponseData> GetNewBlobUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "items/{id:int}/images/new-blob-url")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        var qs          = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var fileName    = qs["fileName"]    ?? "";
        var contentType = qs["contentType"] ?? "image/jpeg";

        if (string.IsNullOrWhiteSpace(fileName))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("fileName query param is required.", ct);
            return bad;
        }

        try
        {
            var info = await _newBlobUrl.HandleAsync(id, fileName, contentType, ct);
            var ok   = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(info, ct);
            return ok;
        }
        catch (ArgumentException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, ct);
            return bad;
        }
        catch (KeyNotFoundException ex)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync(ex.Message, ct);
            return notFound;
        }
        catch (InvalidOperationException ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message, ct);
            return err;
        }
    }

    // ── DELETE /api/items/{id}/images/{imageId} ──────────────────────────────

    [Function("DeleteImage")]
    public async Task<HttpResponseData> DeleteImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete",
            Route = "items/{id:int}/images/{imageId:int}")]
        HttpRequestData req,
        int id,
        int imageId,
        CancellationToken ct)
    {
        try
        {
            await _deleteImage.HandleAsync(id, imageId, ct);
            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (ArgumentException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, ct);
            return bad;
        }
        catch (KeyNotFoundException ex)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync(ex.Message, ct);
            return notFound;
        }
        catch (InvalidOperationException ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message, ct);
            return err;
        }
    }
}