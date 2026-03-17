// Functions/Site/SiteFunctions.cs
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Site;
using LinenLady.Inventory.Contracts;

namespace LinenLady.Inventory.Functions.Site;

public sealed class SiteFunctions
{
    private readonly ListSiteMediaHandler    _listMedia;
    private readonly CreateSiteMediaHandler  _createMedia;
    private readonly DeleteSiteMediaHandler  _deleteMedia;
    private readonly ListSiteConfigHandler   _listConfig;
    private readonly GetSiteConfigHandler    _getConfig;
    private readonly SetSiteConfigHandler    _setConfig;
    private readonly ListHeroSlidesHandler   _listHero;
    private readonly CreateHeroSlideHandler  _createHero;
    private readonly UpdateHeroSlideHandler  _updateHero;
    private readonly DeleteHeroSlideHandler  _deleteHero;
    private readonly ReorderHeroSlidesHandler _reorderHero;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public SiteFunctions(
        ListSiteMediaHandler    listMedia,
        CreateSiteMediaHandler  createMedia,
        DeleteSiteMediaHandler  deleteMedia,
        ListSiteConfigHandler   listConfig,
        GetSiteConfigHandler    getConfig,
        SetSiteConfigHandler    setConfig,
        ListHeroSlidesHandler   listHero,
        CreateHeroSlideHandler  createHero,
        UpdateHeroSlideHandler  updateHero,
        DeleteHeroSlideHandler  deleteHero,
        ReorderHeroSlidesHandler reorderHero)
    {
        _listMedia   = listMedia;
        _createMedia = createMedia;
        _deleteMedia = deleteMedia;
        _listConfig  = listConfig;
        _getConfig   = getConfig;
        _setConfig   = setConfig;
        _listHero    = listHero;
        _createHero  = createHero;
        _updateHero  = updateHero;
        _deleteHero  = deleteHero;
        _reorderHero = reorderHero;
    }

    private static async Task<T?> ReadBody<T>(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(body)
                ? default
                : JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch { return default; }
    }

    // ── Media ─────────────────────────────────────────────────────────────────

    [Function("ListSiteMedia")]
    public async Task<HttpResponseData> ListMedia(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "site/media")] HttpRequestData req,
        CancellationToken ct)
    {
        var result = await _listMedia.HandleAsync(ct);
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    [Function("CreateSiteMedia")]
    public async Task<HttpResponseData> CreateMedia(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "site/media")] HttpRequestData req,
        CancellationToken ct)
    {
        var body = await ReadBody<CreateMediaRequest>(req, ct);
        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid body.", ct);
            return bad;
        }

        try
        {
            var result = await _createMedia.HandleAsync(body, ct);
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
    }

    [Function("DeleteSiteMedia")]
    public async Task<HttpResponseData> DeleteMedia(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "site/media/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        var deleted = await _deleteMedia.HandleAsync(id, ct);
        return req.CreateResponse(deleted ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
    }

    // ── SiteConfig ────────────────────────────────────────────────────────────

    [Function("ListSiteConfig")]
    public async Task<HttpResponseData> ListConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "site/config")] HttpRequestData req,
        CancellationToken ct)
    {
        var result = await _listConfig.HandleAsync(ct);
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    [Function("GetSiteConfig")]
    public async Task<HttpResponseData> GetConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "site/config/{key}")] HttpRequestData req,
        string key,
        CancellationToken ct)
    {
        var result = await _getConfig.HandleAsync(key, ct);
        if (result is null)
        {
            var nf = req.CreateResponse(HttpStatusCode.NotFound);
            await nf.WriteStringAsync($"Config key '{key}' not found.", ct);
            return nf;
        }
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    [Function("SetSiteConfig")]
    public async Task<HttpResponseData> SetConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "site/config/{key}")] HttpRequestData req,
        string key,
        CancellationToken ct)
    {
        var body = await ReadBody<SetConfigRequest>(req, ct);
        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid body.", ct);
            return bad;
        }

        var result = await _setConfig.HandleAsync(key, body.MediaId, ct);
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    // ── Hero Slides ───────────────────────────────────────────────────────────

    [Function("ListHeroSlides")]
    public async Task<HttpResponseData> ListHero(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "site/hero")] HttpRequestData req,
        CancellationToken ct)
    {
        var qs         = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var activeOnly = qs["activeOnly"]?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var result     = await _listHero.HandleAsync(activeOnly, ct);
        var ok         = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    [Function("CreateHeroSlide")]
    public async Task<HttpResponseData> CreateHero(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "site/hero")] HttpRequestData req,
        CancellationToken ct)
    {
        var body = await ReadBody<UpsertHeroSlideRequest>(req, ct);
        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid body.", ct);
            return bad;
        }

        var result = await _createHero.HandleAsync(body, ct);
        var ok = req.CreateResponse(HttpStatusCode.Created);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    [Function("UpdateHeroSlide")]
    public async Task<HttpResponseData> UpdateHero(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "site/hero/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        var body = await ReadBody<UpsertHeroSlideRequest>(req, ct);
        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid body.", ct);
            return bad;
        }

        var result = await _updateHero.HandleAsync(id, body, ct);
        if (result is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(result, ct);
        return ok;
    }

    [Function("DeleteHeroSlide")]
    public async Task<HttpResponseData> DeleteHero(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "site/hero/{id:int}")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        var deleted = await _deleteHero.HandleAsync(id, ct);
        return req.CreateResponse(deleted ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
    }

    [Function("ReorderHeroSlides")]
    public async Task<HttpResponseData> ReorderHero(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "site/hero/reorder")] HttpRequestData req,
        CancellationToken ct)
    {
        var body = await ReadBody<ReorderHeroSlidesRequest>(req, ct);
        if (body is null || body.Slides.Count == 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid body.", ct);
            return bad;
        }

        await _reorderHero.HandleAsync(body.Slides, ct);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }
}