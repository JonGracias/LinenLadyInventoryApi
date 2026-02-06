using System.Net;
using LinenLady.Inventory.Application.Images;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace LinenLady.Inventory.Functions;

public sealed class DeleteItemImage
{
    private readonly DeleteItemImageHandler _handler;

    public DeleteItemImage(DeleteItemImageHandler handler)
    {
        _handler = handler;
    }

    [Function("DeleteItemImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete",
            Route = "items/{id:int}/images/{imageId:int}")]
        HttpRequestData req,
        int id,
        int imageId,
        CancellationToken ct)
    {
        if (id <= 0 || imageId <= 0)
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var result = await _handler.Handle(id, imageId, ct);

        return result switch
        {
            DeleteItemImageResult.Deleted =>
                req.CreateResponse(HttpStatusCode.NoContent),

            DeleteItemImageResult.ItemNotFound =>
                req.CreateResponse(HttpStatusCode.NotFound),

            DeleteItemImageResult.ImageNotFound =>
                req.CreateResponse(HttpStatusCode.NotFound),

            _ =>
                req.CreateResponse(HttpStatusCode.InternalServerError)
        };
    }
}
