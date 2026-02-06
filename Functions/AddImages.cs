using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Images;
using static LinenLady.Inventory.Contracts.AddImagesContracts;

namespace LinenLady.Inventory.Functions;

public sealed class AddImages
{
    private readonly AddImagesHandler _handler;

    public AddImages(AddImagesHandler handler)
    {
        _handler = handler;
    }

    [Function("AddImages")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/{id:int}/images")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        AddImagesRequest? body;
        try { body = await req.ReadFromJsonAsync<AddImagesRequest>(ct); }
        catch { body = null; }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(id, body, ct);

            var resp = req.CreateResponse(HttpStatusCode.Created);
            await resp.WriteAsJsonAsync(result, ct);
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
