// /Functions/SimilarItems.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Search;

namespace LinenLady.Inventory.Functions;

public sealed class SimilarItems
{
    private readonly SimilarItemsHandler _handler;

    public SimilarItems(SimilarItemsHandler handler)
    {
        _handler = handler;
    }

    [Function("SimilarItems")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id:int}/similar")]
        HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        if (id <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id.", ct);
            return bad;
        }

        var qs           = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        int top          = int.TryParse(qs.Get("top"), out var t) ? t : 10;
        bool publishedOnly = qs.Get("publishedOnly") != "false"; // default true
        double minScore = double.TryParse(qs.Get("minScore"), out var s) ? s : 0.0; // default no filter

        try
        {
            var results = await _handler.HandleAsync(id, top, publishedOnly, minScore, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(results, ct);
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