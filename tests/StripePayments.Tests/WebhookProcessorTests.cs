using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StripePayments.Api.Data;
using StripePayments.Api.Services;

namespace StripePayments.Tests;

public sealed class WebhookProcessorTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly PaymentsDbContext _db;
    private readonly WebhookProcessor _processor;

    public WebhookProcessorTests()
    {
        _conn.Open();
        _db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _processor = new WebhookProcessor(_db, TimeProvider.System, NullLogger<WebhookProcessor>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static string Event(string id, string type, string objectJson) =>
        "{\"id\":\"" + id + "\",\"object\":\"event\",\"type\":\"" + type + "\",\"data\":{\"object\":" + objectJson + "}}";

    private void SeedSubscription(string id = "sub_1", string status = "incomplete")
    {
        _db.Subscriptions.Add(new SubscriptionRecord { StripeSubscriptionId = id, StripeCustomerId = "cus_1", PriceId = "price_1", Status = status });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Invoice_paid_activates_the_subscription()
    {
        SeedSubscription();
        var result = await _processor.ProcessAsync(Event("evt_1", "invoice.paid", """{"id":"in_1","subscription":"sub_1"}"""));

        Assert.Equal(WebhookResult.Processed, result);
        Assert.Equal("active", (await _db.Subscriptions.FindAsync("sub_1"))!.Status);
    }

    [Fact]
    public async Task Invoice_paid_reads_the_subscription_from_the_newer_nested_shape()
    {
        SeedSubscription();
        var nested = """{"id":"in_1","parent":{"subscription_details":{"subscription":"sub_1"}}}""";
        var result = await _processor.ProcessAsync(Event("evt_nested", "invoice.paid", nested));

        Assert.Equal(WebhookResult.Processed, result);
        Assert.Equal("active", (await _db.Subscriptions.FindAsync("sub_1"))!.Status);
    }

    [Fact]
    public async Task Payment_failure_marks_the_subscription_past_due()
    {
        SeedSubscription(status: "active");
        await _processor.ProcessAsync(Event("evt_2", "invoice.payment_failed", """{"id":"in_2","subscription":"sub_1"}"""));

        Assert.Equal("past_due", (await _db.Subscriptions.FindAsync("sub_1"))!.Status);
    }

    [Fact]
    public async Task Subscription_deleted_cancels_it()
    {
        SeedSubscription(status: "active");
        await _processor.ProcessAsync(Event("evt_3", "customer.subscription.deleted", """{"id":"sub_1","status":"canceled"}"""));

        Assert.Equal("canceled", (await _db.Subscriptions.FindAsync("sub_1"))!.Status);
    }

    [Fact]
    public async Task Subscription_updated_creates_a_missing_record_with_the_price()
    {
        var json = """{"id":"sub_9","customer":"cus_9","status":"active","items":{"data":[{"price":{"id":"price_9"}}]}}""";
        var result = await _processor.ProcessAsync(Event("evt_4", "customer.subscription.updated", json));

        Assert.Equal(WebhookResult.Processed, result);
        var sub = await _db.Subscriptions.FindAsync("sub_9");
        Assert.Equal("cus_9", sub!.StripeCustomerId);
        Assert.Equal("price_9", sub.PriceId);
        Assert.Equal("active", sub.Status);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_applied_only_once()
    {
        SeedSubscription();
        var payload = Event("evt_dup", "invoice.paid", """{"id":"in_1","subscription":"sub_1"}""");

        var first = await _processor.ProcessAsync(payload);

        // Simulate a later change, then Stripe retrying the old event. The retry must not overwrite it.
        var sub = await _db.Subscriptions.FindAsync("sub_1");
        sub!.Status = "canceled";
        await _db.SaveChangesAsync();
        var second = await _processor.ProcessAsync(payload);

        Assert.Equal(WebhookResult.Processed, first);
        Assert.Equal(WebhookResult.Duplicate, second);
        Assert.Equal("canceled", (await _db.Subscriptions.FindAsync("sub_1"))!.Status);
        Assert.Equal(1, await _db.ProcessedEvents.CountAsync(e => e.EventId == "evt_dup"));
    }

    [Fact]
    public async Task Ach_payment_moves_from_processing_to_succeeded()
    {
        var obj = """{"id":"pi_1","customer":"cus_1","amount":2500}""";
        await _processor.ProcessAsync(Event("evt_5", "payment_intent.processing", obj));
        Assert.Equal("processing", (await _db.AchPayments.FindAsync("pi_1"))!.Status);

        await _processor.ProcessAsync(Event("evt_6", "payment_intent.succeeded", obj));
        var payment = await _db.AchPayments.FindAsync("pi_1");
        Assert.Equal("succeeded", payment!.Status);
        Assert.Equal(2500, payment.AmountCents);
    }

    [Fact]
    public async Task Ach_payment_failure_is_recorded()
    {
        await _processor.ProcessAsync(Event("evt_7", "payment_intent.payment_failed", """{"id":"pi_2","customer":"cus_1","amount":900}"""));

        Assert.Equal("failed", (await _db.AchPayments.FindAsync("pi_2"))!.Status);
    }

    [Fact]
    public async Task Unknown_event_types_are_ignored_but_recorded()
    {
        var result = await _processor.ProcessAsync(Event("evt_8", "charge.dispute.created", """{"id":"dp_1"}"""));

        Assert.Equal(WebhookResult.Ignored, result);
        Assert.True(await _db.ProcessedEvents.AnyAsync(e => e.EventId == "evt_8"));
    }

    [Fact]
    public async Task An_invoice_for_an_unknown_subscription_is_ignored_without_failing()
    {
        var result = await _processor.ProcessAsync(Event("evt_9", "invoice.paid", """{"id":"in_9","subscription":"sub_missing"}"""));

        Assert.Equal(WebhookResult.Ignored, result);
    }
}
