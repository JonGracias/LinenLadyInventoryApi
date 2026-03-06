// Functions/UpdateItem.cs
using System.Net;
using System.Text.Json;
using LinenLady.Inventory.Application.Items;
using LinenLady.Inventory.Functions.Contracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace LinenLady.Inventory.Functions;

public sealed class UpdateItem
{
    private readonly UpdateItemHandler _handler;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public UpdateItem(UpdateItemHandler handler)
    {
        _handler = handler;
    }

    [Function("UpdateItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch",
            Route = "items/{id:int}")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
            return req.CreateResponse(HttpStatusCode.BadRequest);

        UpdateItemRequest? body;
        try
        {
            var bodyStr = await req.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(bodyStr))
                return req.CreateResponse(HttpStatusCode.BadRequest);

            body = JsonSerializer.Deserialize<UpdateItemRequest>(bodyStr, JsonOptions);
            if (body is null)
                return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        catch
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var (result, response) = await _handler.Handle(id, body, ct);

        switch (result)
        {
            case UpdateItemResult.Updated:
                var okResp = req.CreateResponse(HttpStatusCode.OK);
                await okResp.WriteAsJsonAsync(response, ct);
                return okResp;

            case UpdateItemResult.NotFound:
                return req.CreateResponse(HttpStatusCode.NotFound);

            case UpdateItemResult.BadRequest:
                return req.CreateResponse(HttpStatusCode.BadRequest);

            default:
                return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }
}