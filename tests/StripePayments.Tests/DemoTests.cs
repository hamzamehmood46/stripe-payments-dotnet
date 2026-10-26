using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using StripePayments.Api.Demo;

namespace StripePayments.Tests;

public class DemoFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"demo-{Guid.NewGuid():N}.db");

    protected virtual bool Demo => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Demo:Enabled", Demo ? "true" : "false");
        builder.UseSetting("ConnectionStrings:Payments", $"Data Source={_db}");
    }
}

public sealed class RealModeFactory : DemoFactory
{
    protected override bool Demo => false;
}

public class DemoApiTests : IClassFixture<DemoFactory>
{
    private readonly HttpClient _http;

    public DemoApiTests(DemoFactory factory) => _http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<JsonElement> Post(string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await _http.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> NewSubscription()
    {
        var customer = await Post("/customers", new { email = $"{Guid.NewGuid():N}@example.com" });
        var sub = await Post("/subscriptions", new { customerId = customer.GetProperty("customerId").GetString(), priceId = "price_demo" });
        return sub.GetProperty("subscriptionId").GetString()!;
    }

    private async Task<string> NewPayment(string key)
    {
        var customer = await Post("/customers", new { email = $"{Guid.NewGuid():N}@example.com" });
        var payment = await Post("/payments/ach", new { customerId = customer.GetProperty("customerId").GetString(), amountCents = 5000 }, key);
        return payment.GetProperty("paymentIntentId").GetString()!;
    }

    private Task<JsonElement> Simulate(string type, string objectId, string? eventId = null, bool? tamper = null, int? age = null) =>
        Post("/demo/simulate", new { type, objectId, eventId, tamper, ageSeconds = age });

    [Fact]
    public async Task The_root_opens_swagger_and_swagger_loads()
    {
        var root = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/swagger", root.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    [Fact]
    public async Task A_new_subscription_starts_incomplete_and_a_paid_invoice_activates_it()
    {
        var sub = await NewSubscription();
        Assert.StartsWith("sub_demo_", sub);
        Assert.Equal("incomplete", (await _http.GetFromJsonAsync<JsonElement>($"/subscriptions/{sub}")).GetProperty("status").GetString());

        var result = await Simulate("invoice.paid", sub);

        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal("Processed", result.GetProperty("result").GetString());
        Assert.Equal("active", result.GetProperty("subscriptionStatus").GetString());
        Assert.Equal("active", (await _http.GetFromJsonAsync<JsonElement>($"/subscriptions/{sub}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_failed_invoice_makes_it_past_due_and_a_deleted_subscription_cancels_it()
    {
        var sub = await NewSubscription();
        await Simulate("invoice.paid", sub);

        Assert.Equal("past_due", (await Simulate("invoice.payment_failed", sub)).GetProperty("subscriptionStatus").GetString());
        Assert.Equal("canceled", (await Simulate("customer.subscription.deleted", sub)).GetProperty("subscriptionStatus").GetString());
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_applied_once()
    {
        var sub = await NewSubscription();
        var first = await Simulate("invoice.paid", sub, "evt_fixed_1");
        await Simulate("invoice.payment_failed", sub);
        var again = await Simulate("invoice.paid", sub, "evt_fixed_1");

        Assert.Equal("Processed", first.GetProperty("result").GetString());
        Assert.Equal("Duplicate", again.GetProperty("result").GetString());
        Assert.Equal("past_due", again.GetProperty("subscriptionStatus").GetString());
    }

    [Fact]
    public async Task A_tampered_payload_is_refused_and_changes_nothing()
    {
        var sub = await NewSubscription();

        var result = await Simulate("invoice.paid", sub, tamper: true);

        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal(400, result.GetProperty("httpStatus").GetInt32());
        Assert.Equal("incomplete", (await _http.GetFromJsonAsync<JsonElement>($"/subscriptions/{sub}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_old_timestamp_is_refused_as_a_possible_replay()
    {
        var sub = await NewSubscription();

        var result = await Simulate("invoice.paid", sub, age: 3600);

        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal("incomplete", (await _http.GetFromJsonAsync<JsonElement>($"/subscriptions/{sub}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_ach_payment_moves_through_processing_to_succeeded_or_failed()
    {
        var ok = await NewPayment("ach-ok-1");
        Assert.Equal("processing", (await Simulate("payment_intent.processing", ok)).GetProperty("paymentStatus").GetString());
        Assert.Equal("succeeded", (await Simulate("payment_intent.succeeded", ok)).GetProperty("paymentStatus").GetString());

        var bad = await NewPayment("ach-bad-1");
        Assert.Equal("failed", (await Simulate("payment_intent.payment_failed", bad)).GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task Retrying_an_ach_payment_with_the_same_idempotency_key_returns_the_same_payment()
    {
        var customer = (await Post("/customers", new { email = "retry@example.com" }, "cust-retry")).GetProperty("customerId").GetString();
        var first = await Post("/payments/ach", new { customerId = customer, amountCents = 7000 }, "retry-key");
        var second = await Post("/payments/ach", new { customerId = customer, amountCents = 7000 }, "retry-key");
        var other = await Post("/payments/ach", new { customerId = customer, amountCents = 7000 }, "other-key");

        Assert.Equal(first.GetProperty("paymentIntentId").GetString(), second.GetProperty("paymentIntentId").GetString());
        Assert.NotEqual(first.GetProperty("paymentIntentId").GetString(), other.GetProperty("paymentIntentId").GetString());
    }

    [Fact]
    public async Task An_ach_payment_without_an_idempotency_key_is_refused()
    {
        var response = await _http.PostAsJsonAsync("/payments/ach", new { customerId = "cus_demo_x", amountCents = 5000 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("nonsense.event", "sub_1")]
    [InlineData("invoice.paid", "")]
    public async Task Bad_simulate_requests_are_a_400(string type, string objectId)
    {
        var response = await _http.PostAsJsonAsync("/demo/simulate", new { type, objectId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_event_for_an_unknown_subscription_is_ignored_not_an_error()
    {
        var result = await Simulate("invoice.paid", "sub_demo_unknown");

        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal("Ignored", result.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Outside_demo_mode_the_simulator_does_not_exist()
    {
        using var factory = new RealModeFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/demo/simulate", new { type = "invoice.paid", objectId = "sub_1" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public class DemoStripeTests
{
    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_objects_and_new_keys_return_new_ones()
    {
        var gateway = new DemoStripeGateway();

        var a = await gateway.CreateCustomerAsync("a@x.com", null, "k1", default);
        var b = await gateway.CreateCustomerAsync("a@x.com", null, "k1", default);
        var c = await gateway.CreateCustomerAsync("a@x.com", null, "k2", default);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(await gateway.CreateSubscriptionAsync("cus", "price", "s1", default), await gateway.CreateSubscriptionAsync("cus", "price", "s1", default));
    }

    [Fact]
    public void Every_simulator_event_type_builds_valid_json_with_the_object_id()
    {
        foreach (var type in DemoEndpoints.Types)
        {
            var json = DemoEndpoints.BuildEvent("evt_1", type, "obj_1");
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(type, doc.RootElement.GetProperty("type").GetString());
            Assert.Contains("obj_1", json);
        }
    }

    [Fact]
    public void Signing_follows_the_stripe_scheme()
    {
        var header = DemoEndpoints.Sign("whsec_x", 1700000000, "{}");

        Assert.StartsWith("t=1700000000,v1=", header);
        Assert.Equal(header, DemoEndpoints.Sign("whsec_x", 1700000000, "{}"));
        Assert.NotEqual(header, DemoEndpoints.Sign("whsec_y", 1700000000, "{}"));
    }
}
