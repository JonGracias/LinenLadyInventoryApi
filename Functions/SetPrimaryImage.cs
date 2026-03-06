// /Functions/SetPrimaryImage.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Images;

namespace LinenLady.Inventory.Functions;

public sealed class SetPrimaryImage
{
    private readonly SetPrimaryImageHandler _handler;

    public SetPrimaryImage(SetPrimaryImageHandler handler)
    {
        _handler = handler;
    }

    [Function("SetPrimaryImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "items/{id:int}/images/{imageId:int}/primary")] HttpRequestData req,
        int id,
        int imageId,
        CancellationToken ct)
    {
        try
        {
            await _handler.HandleAsync(id, imageId, ct);

            var resp = req.CreateResponse(HttpStatusCode.NoContent);
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