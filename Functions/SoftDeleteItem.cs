// Functions/SoftDeleteItem.cs
using System.Net;
using LinenLady.Inventory.Application.Items;
using LinenLady.Inventory.Functions.Contracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace LinenLady.Inventory.Functions;

public sealed class SoftDeleteItem
{
    private readonly SoftDeleteItemHandler _handler;

    public SoftDeleteItem(SoftDeleteItemHandler handler)
    {
        _handler = handler;
    }

    [Function("SoftDeleteItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete",
            Route = "items/{id:int}")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var result = await _handler.Handle(id, ct);

        return result switch
        {
            SoftDeleteItemResult.Deleted =>
                req.CreateResponse(HttpStatusCode.NoContent),

            SoftDeleteItemResult.NotFound =>
                req.CreateResponse(HttpStatusCode.NotFound),

            _ => req.CreateResponse(HttpStatusCode.InternalServerError),
        };
    }
}