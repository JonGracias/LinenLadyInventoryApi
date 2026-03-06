// /Functions/ReplaceImage.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Images;
using LinenLady.Inventory.Functions.Infrastructure.Blob;

namespace LinenLady.Inventory.Functions;

public sealed class ReplaceImage
{
    private readonly ReplaceImageHandler _handler;

    public ReplaceImage(ReplaceImageHandler handler)
    {
        _handler = handler;
    }

    [Function("ReplaceImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}/images/{imageId:int}/replace-url")] HttpRequestData req,
        int id,
        int imageId,
        CancellationToken ct)
    {
        try
        {
            var info = await _handler.HandleAsync(id, imageId, ct);

            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteAsJsonAsync(new
            {
                UploadUrl       = info.UploadUrl,
                RequiredHeaders = info.RequiredHeaders,
                ContentType     = info.ContentType,
                BlobName        = info.BlobName,
            }, ct);
            return resp;
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