// Functions/Customers/CustomerFunctions.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using LinenLady.Inventory.Application.Customers;
using LinenLady.Contracts;

namespace LinenLady.Inventory.Functions.Customers;

public sealed class SyncCustomer
{
    private readonly SyncCustomerHandler _handler;
    public SyncCustomer(SyncCustomerHandler handler) => _handler = handler;

    [Function("SyncCustomer")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers/sync")] HttpRequestData req,
        CancellationToken ct)
    {
        UpsertCustomerRequest? body;
        try { body = await req.ReadFromJsonAsync<UpsertCustomerRequest>(ct); }
        catch { body = null; }

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

// ─────────────────────────────────────────────────────────────

public sealed class GetMyProfile
{
    private readonly GetMyProfileHandler _handler;
    public GetMyProfile(GetMyProfileHandler handler) => _handler = handler;

    [Function("GetMyProfile")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/me")] HttpRequestData req,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        try
        {
            var result = await _handler.HandleAsync(clerkUserId, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class UpdateMyProfile
{
    private readonly UpdateProfileHandler _handler;
    public UpdateMyProfile(UpdateProfileHandler handler) => _handler = handler;

    [Function("UpdateMyProfile")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "customers/me")] HttpRequestData req,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        UpdateCustomerRequest? body;
        try { body = await req.ReadFromJsonAsync<UpdateCustomerRequest>(ct); }
        catch { body = null; }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(clerkUserId, body, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class UpsertAddress
{
    private readonly UpsertAddressHandler _handler;
    public UpsertAddress(UpsertAddressHandler handler) => _handler = handler;

    // POST  /api/customers/me/addresses       — new address
    // PUT   /api/customers/me/addresses/{id}  — update existing
    [Function("UpsertAddress")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "put",
            Route = "customers/me/addresses/{addressId?}")] HttpRequestData req,
        string? addressId,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        UpsertAddressRequest? body;
        try { body = await req.ReadFromJsonAsync<UpsertAddressRequest>(ct); }
        catch { body = null; }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        int? addrId = int.TryParse(addressId, out var parsed) ? parsed : null;

        try
        {
            var result = await _handler.HandleAsync(clerkUserId, body, addrId, ct);
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
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class DeleteAddress
{
    private readonly DeleteAddressHandler _handler;
    public DeleteAddress(DeleteAddressHandler handler) => _handler = handler;

    [Function("DeleteAddress")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete",
            Route = "customers/me/addresses/{addressId:int}")] HttpRequestData req,
        int addressId,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        try
        {
            var deleted = await _handler.HandleAsync(clerkUserId, addressId, ct);
            return req.CreateResponse(deleted ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class SetPreferences
{
    private readonly SetPreferencesHandler _handler;
    public SetPreferences(SetPreferencesHandler handler) => _handler = handler;

    [Function("SetPreferences")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put",
            Route = "customers/me/preferences")] HttpRequestData req,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        SetPreferencesRequest? body;
        try { body = await req.ReadFromJsonAsync<SetPreferencesRequest>(ct); }
        catch { body = null; }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(clerkUserId, body, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class CreateReservation
{
    private readonly CreateReservationHandler _handler;
    public CreateReservation(CreateReservationHandler handler) => _handler = handler;

    [Function("CreateReservation")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "reservations")] HttpRequestData req,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        CreateReservationRequest? body;
        try { body = await req.ReadFromJsonAsync<CreateReservationRequest>(ct); }
        catch { body = null; }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.HandleAsync(clerkUserId, body, ct);
            var created = req.CreateResponse(HttpStatusCode.Created);
            await created.WriteAsJsonAsync(result, ct);
            return created;
        }
        catch (ArgumentException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, ct);
            return bad;
        }
        catch (EmailNotVerifiedException ex)
        {
            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteStringAsync(ex.Message, ct);
            return forbidden;
        }
        catch (ItemAlreadyReservedException ex)
        {
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteStringAsync(ex.Message, ct);
            return conflict;
        }
        catch (ItemNotFoundException ex)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync(ex.Message, ct);
            return notFound;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class CancelReservation
{
    private readonly CancelReservationHandler _handler;
    public CancelReservation(CancelReservationHandler handler) => _handler = handler;

    [Function("CancelReservation")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch",
            Route = "reservations/{reservationId:int}/cancel")] HttpRequestData req,
        int reservationId,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        try
        {
            var result = await _handler.HandleAsync(clerkUserId, reservationId, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (ReservationNotFoundException ex)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync(ex.Message, ct);
            return notFound;
        }
        catch (ReservationConflictException ex)
        {
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteStringAsync(ex.Message, ct);
            return conflict;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class SquareWebhook
{
    private readonly SquareWebhookHandler _handler;
    public SquareWebhook(SquareWebhookHandler handler) => _handler = handler;

    [Function("SquareWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "square/webhook")] HttpRequestData req,
        CancellationToken ct)
    {
        string body;
        try { body = await req.ReadAsStringAsync() ?? string.Empty; }
        catch { body = string.Empty; }

        if (string.IsNullOrWhiteSpace(body))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        try
        {
            await _handler.HandleAsync(body, ct);
            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (InvalidOperationException ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message, ct);
            return err;
        }
    }
}

// ─────────────────────────────────────────────────────────────

public sealed class GetMessages
{
    private readonly MessageHandler _handler;
    public GetMessages(MessageHandler handler) => _handler = handler;

    [Function("GetMessages")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "customers/me/messages")] HttpRequestData req,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        try
        {
            var result = await _handler.GetAsync(clerkUserId, ct);
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(result, ct);
            return ok;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class SendMessage
{
    private readonly MessageHandler _handler;
    public SendMessage(MessageHandler handler) => _handler = handler;

    [Function("SendMessage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "customers/me/messages")] HttpRequestData req,
        CancellationToken ct)
    {
        var clerkUserId = FunctionHelpers.GetClerkUserId(req);
        if (clerkUserId is null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        SendMessageRequest? body;
        try { body = await req.ReadFromJsonAsync<SendMessageRequest>(ct); }
        catch { body = null; }

        if (body is null || string.IsNullOrWhiteSpace(body.Body))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Message body is required.", ct);
            return bad;
        }

        try
        {
            var result = await _handler.SendAsync(clerkUserId, body, ct);
            var created = req.CreateResponse(HttpStatusCode.Created);
            await created.WriteAsJsonAsync(result, ct);
            return created;
        }
        catch (ArgumentException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, ct);
            return bad;
        }
        catch (CustomerNotFoundException ex)
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

// ─────────────────────────────────────────────────────────────

public sealed class ExpireReservations
{
    private readonly ExpireReservationsHandler _handler;
    public ExpireReservations(ExpireReservationsHandler handler) => _handler = handler;

    [Function("ExpireReservations")]
    public async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        await _handler.HandleAsync(ct);
    }
}

// ─────────────────────────────────────────────────────────────
// Shared helper — extracts Clerk user ID from request headers.
// The Next.js middleware forwards this after validating the JWT.
// ─────────────────────────────────────────────────────────────

file static class FunctionHelpers
{
    internal static string? GetClerkUserId(HttpRequestData req)
    {
        if (!req.Headers.TryGetValues("X-Clerk-User-Id", out var vals))
            return null;
        var id = vals.FirstOrDefault();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}