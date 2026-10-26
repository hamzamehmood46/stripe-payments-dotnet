using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StripePayments.Api.Data;

namespace StripePayments.Api.Services;

public enum WebhookResult { Processed, Duplicate, Ignored }

/// <summary>
/// Applies Stripe events to local state. Each event id is recorded in the same transaction as its effects,
/// so a retry or replay of the same event can never be applied twice.
/// </summary>
public class WebhookProcessor(PaymentsDbContext db, TimeProvider clock, ILogger<WebhookProcessor> log)
{
    public async Task<WebhookResult> ProcessAsync(string rawJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var eventId = root.GetProperty("id").GetString() ?? throw new InvalidOperationException("event id missing");
        var type = root.GetProperty("type").GetString() ?? "";
        var obj = root.GetProperty("data").GetProperty("object");

        if (await db.ProcessedEvents.AnyAsync(e => e.EventId == eventId, ct))
        {
            log.LogInformation("Duplicate Stripe event {EventId} ({Type}) skipped", eventId, type);
            return WebhookResult.Duplicate;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var handled = await ApplyAsync(type, obj, now, ct);
        db.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, Type = type, ProcessedUtc = now });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two deliveries of the same event raced, and the other one won the primary key.
            await tx.RollbackAsync(ct);
            return WebhookResult.Duplicate;
        }
        return handled ? WebhookResult.Processed : WebhookResult.Ignored;
    }

    private async Task<bool> ApplyAsync(string type, JsonElement obj, DateTime now, CancellationToken ct)
    {
        switch (type)
        {
            case "invoice.paid":
            case "invoice.payment_succeeded":
                return await SetSubscriptionStatusAsync(InvoiceSubscriptionId(obj), "active", now, ct);
            case "invoice.payment_failed":
                return await SetSubscriptionStatusAsync(InvoiceSubscriptionId(obj), "past_due", now, ct);
            case "customer.subscription.created":
            case "customer.subscription.updated":
                return await UpsertSubscriptionAsync(obj, now, ct);
            case "customer.subscription.deleted":
                return await SetSubscriptionStatusAsync(Str(obj, "id"), "canceled", now, ct);
            case "payment_intent.processing":
                return await SetAchStatusAsync(obj, "processing", now, ct);
            case "payment_intent.succeeded":
                return await SetAchStatusAsync(obj, "succeeded", now, ct);
            case "payment_intent.payment_failed":
                return await SetAchStatusAsync(obj, "failed", now, ct);
            default:
                return false;
        }
    }

    private async Task<bool> SetSubscriptionStatusAsync(string? subscriptionId, string status, DateTime now, CancellationToken ct)
    {
        if (subscriptionId is null) return false;
        var sub = await db.Subscriptions.FindAsync([subscriptionId], ct);
        if (sub is null) return false;
        sub.Status = status;
        sub.UpdatedUtc = now;
        return true;
    }

    private async Task<bool> UpsertSubscriptionAsync(JsonElement obj, DateTime now, CancellationToken ct)
    {
        var id = Str(obj, "id");
        if (id is null) return false;
        var sub = await db.Subscriptions.FindAsync([id], ct);
        if (sub is null)
        {
            sub = new SubscriptionRecord { StripeSubscriptionId = id };
            db.Subscriptions.Add(sub);
        }
        sub.StripeCustomerId = Str(obj, "customer") ?? sub.StripeCustomerId;
        sub.Status = Str(obj, "status") ?? sub.Status;
        sub.PriceId = FirstPriceId(obj) ?? sub.PriceId;
        sub.UpdatedUtc = now;
        return true;
    }

    private async Task<bool> SetAchStatusAsync(JsonElement obj, string status, DateTime now, CancellationToken ct)
    {
        var id = Str(obj, "id");
        if (id is null) return false;
        var payment = await db.AchPayments.FindAsync([id], ct);
        if (payment is null)
        {
            payment = new AchPayment
            {
                PaymentIntentId = id,
                StripeCustomerId = Str(obj, "customer") ?? "",
                AmountCents = obj.TryGetProperty("amount", out var a) && a.TryGetInt64(out var cents) ? cents : 0,
            };
            db.AchPayments.Add(payment);
        }
        payment.Status = status;
        payment.UpdatedUtc = now;
        return true;
    }

    // Older API versions put the subscription id on the invoice; newer ones nest it under parent.subscription_details.
    private static string? InvoiceSubscriptionId(JsonElement invoice)
    {
        var direct = Str(invoice, "subscription");
        if (direct is not null) return direct;
        if (invoice.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty("subscription_details", out var details) && details.ValueKind == JsonValueKind.Object)
            return Str(details, "subscription");
        return null;
    }

    private static string? FirstPriceId(JsonElement subscription)
    {
        if (subscription.TryGetProperty("items", out var items) && items.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            && data[0].TryGetProperty("price", out var price))
            return Str(price, "id");
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
