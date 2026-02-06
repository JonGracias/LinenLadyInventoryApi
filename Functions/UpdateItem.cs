using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Items;

namespace LinenLady.Inventory.Functions;

public sealed class UpdateItem
{
    private readonly UpdateItemHandler _handler;

    public UpdateItem(UpdateItemHandler handler)
    {
        _handler = handler;
    }

    [Function("UpdateItem")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "put", Route = "items/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        JsonDocument? doc = null;
        try { doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        using (doc)
        {
            try
            {
                var dto = await _handler.HandleAsync(id, doc!.RootElement, ct);

                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteAsJsonAsync(dto, ct);
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
                var nf = req.CreateResponse(HttpStatusCode.NotFound);
                await nf.WriteStringAsync(ex.Message, ct);
                return nf;
            }
            catch (InvalidOperationException ex)
            {
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync(ex.Message, ct);
                return err;
            }
        }
    }
}
