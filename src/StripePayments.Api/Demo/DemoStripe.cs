using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StripePayments.Api.Data;
using StripePayments.Api.Services;

namespace StripePayments.Api.Demo;

/// <summary>
/// A stand-in for Stripe used by the public demo, so nobody needs (or can misuse) an API key. It behaves like
/// Stripe where it matters here: the same idempotency key always returns the same object, so a client retry can never
/// create a second customer, subscription or charge.
/// </summary>
public sealed class DemoStripeGateway : IStripeGateway
{
    private readonly ConcurrentDictionary<string, object> _byKey = new();

    public Task<CreatedCustomer> CreateCustomerAsync(string email, string? name, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult((CreatedCustomer)_byKey.GetOrAdd("customer:" + idempotencyKey, _ => new CreatedCustomer(NewId("cus"))));

    public Task<CreatedSubscription> CreateSubscriptionAsync(string customerId, string priceId, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult((CreatedSubscription)_byKey.GetOrAdd("sub:" + idempotencyKey, _ => new CreatedSubscription(NewId("sub"), "incomplete", NewId("in"))));

    public Task<CreatedAchPayment> CreateAchPaymentAsync(string customerId, long amountCents, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult((CreatedAchPayment)_byKey.GetOrAdd("ach:" + idempotencyKey, _ => new CreatedAchPayment(NewId("pi"), "requires_payment_method", NewId("pi") + "_secret_demo")));

    private static string NewId(string prefix) => prefix + "_demo_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(7)).ToLowerInvariant();
}

public sealed record SimulateRequest(string? Type, string? ObjectId, string? EventId, bool? Tamper, int? AgeSeconds);

/// <summary>
/// Lets a visitor play Stripe: it builds an event of the chosen type, signs it the way Stripe does, and delivers it
/// through exactly the same verification and processing as a real webhook. Tampering and old timestamps show the refusals,
/// and sending the same event id twice shows that it is only applied once.
/// </summary>
public static class DemoEndpoints
{
    public static readonly string[] Types =
    [
        "invoice.paid", "invoice.payment_failed", "customer.subscription.deleted",
        "payment_intent.processing", "payment_intent.succeeded", "payment_intent.payment_failed",
    ];

    public static string Sign(string secret, long timestamp, string payload) =>
        $"t={timestamp},v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant()}";

    public static string BuildEvent(string eventId, string type, string objectId)
    {
        var obj = type switch
        {
            "invoice.paid" or "invoice.payment_failed" => $"{{\"id\":\"in_demo\",\"object\":\"invoice\",\"subscription\":\"{objectId}\"}}",
            "customer.subscription.deleted" => $"{{\"id\":\"{objectId}\",\"object\":\"subscription\",\"status\":\"canceled\"}}",
            _ => $"{{\"id\":\"{objectId}\",\"object\":\"payment_intent\"}}",
        };
        return $"{{\"id\":\"{eventId}\",\"object\":\"event\",\"type\":\"{type}\",\"data\":{{\"object\":{obj}}}}}";
    }

    public static void Map(WebApplication app, string webhookSecret)
    {
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

        app.MapPost("/demo/simulate", async (SimulateRequest req, IWebhookSignatureVerifier verifier, WebhookProcessor processor, PaymentsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (req.Type is null || !Types.Contains(req.Type))
                return Results.BadRequest(new { error = $"type must be one of: {string.Join(", ", Types)}" });
            if (string.IsNullOrWhiteSpace(req.ObjectId))
                return Results.BadRequest(new { error = "objectId is the subscription id (sub_...) for invoice and subscription events, or the payment intent id (pi_...) for payment events." });

            var eventId = string.IsNullOrWhiteSpace(req.EventId) ? "evt_demo_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() : req.EventId;
            var payload = BuildEvent(eventId, req.Type, req.ObjectId);
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds() - Math.Clamp(req.AgeSeconds ?? 0, 0, 86400);
            var header = Sign(webhookSecret, timestamp, payload);
            if (req.Tamper == true)
                payload = payload.Replace(req.ObjectId, req.ObjectId + "x");

            if (!verifier.IsValid(payload, header))
                return Results.Json(new { eventId, verified = false, httpStatus = 400, result = "Rejected: invalid signature or expired timestamp. Nothing was changed." });

            var result = await processor.ProcessAsync(payload, ct);
            var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.StripeSubscriptionId == req.ObjectId, ct);
            var payment = await db.AchPayments.AsNoTracking().FirstOrDefaultAsync(p => p.PaymentIntentId == req.ObjectId, ct);
            return Results.Json(new { eventId, verified = true, httpStatus = 200, result = result.ToString(), subscriptionStatus = subscription?.Status, paymentStatus = payment?.Status });
        }).WithTags("Demo");
    }
}
