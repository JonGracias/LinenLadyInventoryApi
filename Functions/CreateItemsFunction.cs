using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Items;
using static LinenLady.Inventory.Contracts.CreateItemsContracts;

namespace LinenLady.Inventory.Functions;

public sealed class CreateItems
{
    private readonly CreateItemsHandler _handler;

    public CreateItems(CreateItemsHandler handler)
    {
        _handler = handler;
    }

    // POST /api/items/drafts  (your current route behavior)
    [Function("CreateItems")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/drafts")] HttpRequestData req,
        CancellationToken ct)
    {
        CreateItemsRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<CreateItemsRequest>(ct);
        }
        catch
        {
            body = null;
        }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(body, ct);

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (ArgumentException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, ct);
            return bad;
        }
        catch (InvalidOperationException ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message, ct);
            return err;
        }
    }
}
