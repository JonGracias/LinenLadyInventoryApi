using System.Net;
using LinenLady.Inventory.Application.Items;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace LinenLady.Inventory.Functions.Functions;

public sealed class DeleteItem
{
    private readonly DeleteItemHandler _handler;

    public DeleteItem(DeleteItemHandler handler)
    {
        _handler = handler;
    }

    [Function("DeleteItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "items/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var result = await _handler.Handle(id, ct);

        return result switch
        {
            DeleteItemResult.Deleted => req.CreateResponse(HttpStatusCode.NoContent),
            DeleteItemResult.NotFound => req.CreateResponse(HttpStatusCode.NotFound),
            _ => req.CreateResponse(HttpStatusCode.InternalServerError),
        };
    }
}
