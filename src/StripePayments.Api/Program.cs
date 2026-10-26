using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Stripe;
using StripePayments.Api.Data;
using StripePayments.Api.Demo;
using StripePayments.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Test-mode keys only. Set them with `dotnet user-secrets` or environment variables (Stripe__SecretKey, Stripe__WebhookSecret).
var secretKey = builder.Configuration["Stripe:SecretKey"] ?? "";
var webhookSecret = builder.Configuration["Stripe:WebhookSecret"] ?? "";

// Demo mode (Demo:Enabled=true) replaces Stripe with a simulator, so a public copy needs no keys and cannot spend or touch anything.
var demo = builder.Configuration.GetValue<bool>("Demo:Enabled");
if (demo)
{
    secretKey = "";
    webhookSecret = "whsec_demo_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

builder.Services.AddDbContext<PaymentsDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Payments") ?? "Data Source=payments.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new StripeClient(string.IsNullOrWhiteSpace(secretKey) ? "sk_test_not_configured" : secretKey));
if (demo)
    builder.Services.AddSingleton<IStripeGateway, DemoStripeGateway>();
else
    builder.Services.AddSingleton<IStripeGateway, StripeGateway>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "Stripe Payments",
    Version = "v1",
    Description = demo
        ? "Public demo: Stripe is simulated, no keys, no money. 1) POST /customers, 2) POST /subscriptions (or POST /payments/ach with an Idempotency-Key), 3) POST /demo/simulate to play Stripe and send a signed webhook for that id. Repeat the same eventId to see it applied once, set tamper or ageSeconds to see it refused."
        : "Stripe test-mode integration: subscriptions, ACH and signed idempotent webhooks.",
}));
builder.Services.AddSingleton<IWebhookSignatureVerifier>(new WebhookSignatureVerifier(webhookSecret));
builder.Services.AddScoped<WebhookProcessor>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreated();

if (!secretKey.StartsWith("sk_test_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(secretKey))
    throw new InvalidOperationException("Refusing to start: only Stripe test-mode keys (sk_test_...) are allowed in this project.");

if (demo || app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (demo)
    DemoEndpoints.Map(app, webhookSecret);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/customers", async (CreateCustomerRequest req, PaymentsDbContext db, IStripeGateway stripe,
    HttpRequest http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
        return Results.BadRequest(new { error = "A valid email is required." });

    var key = http.Headers["Idempotency-Key"].FirstOrDefault() ?? $"customer-{req.Email.ToLowerInvariant()}";
    var created = await stripe.CreateCustomerAsync(req.Email, req.Name, key, ct);

    if (!await db.Customers.AnyAsync(c => c.StripeCustomerId == created.Id, ct))
    {
        db.Customers.Add(new StripeCustomer { Email = req.Email, Name = req.Name, StripeCustomerId = created.Id });
        await db.SaveChangesAsync(ct);
    }
    return Results.Created($"/customers/{created.Id}", new { customerId = created.Id });
});

app.MapPost("/subscriptions", async (CreateSubscriptionRequest req, PaymentsDbContext db, IStripeGateway stripe,
    HttpRequest http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CustomerId) || string.IsNullOrWhiteSpace(req.PriceId))
        return Results.BadRequest(new { error = "customerId and priceId are required." });

    var key = http.Headers["Idempotency-Key"].FirstOrDefault() ?? $"sub-{req.CustomerId}-{req.PriceId}";
    var sub = await stripe.CreateSubscriptionAsync(req.CustomerId, req.PriceId, key, ct);

    if (await db.Subscriptions.FindAsync([sub.Id], ct) is null)
    {
        db.Subscriptions.Add(new SubscriptionRecord
        {
            StripeSubscriptionId = sub.Id,
            StripeCustomerId = req.CustomerId,
            PriceId = req.PriceId,
            Status = sub.Status,
            UpdatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
    return Results.Created($"/subscriptions/{sub.Id}", new { subscriptionId = sub.Id, status = sub.Status });
});

app.MapGet("/subscriptions/{id}", async (string id, PaymentsDbContext db, CancellationToken ct) =>
    await db.Subscriptions.FindAsync([id], ct) is { } s ? Results.Ok(s) : Results.NotFound());

app.MapPost("/payments/ach", async (CreateAchPaymentRequest req, PaymentsDbContext db, IStripeGateway stripe,
    HttpRequest http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CustomerId) || req.AmountCents < 50)
        return Results.BadRequest(new { error = "customerId and an amount of at least 50 cents are required." });

    var key = http.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new { error = "An Idempotency-Key header is required so a retry cannot charge twice." });

    var payment = await stripe.CreateAchPaymentAsync(req.CustomerId, req.AmountCents, key, ct);
    if (await db.AchPayments.FindAsync([payment.Id], ct) is null)
    {
        db.AchPayments.Add(new AchPayment
        {
            PaymentIntentId = payment.Id,
            StripeCustomerId = req.CustomerId,
            AmountCents = req.AmountCents,
            Status = payment.Status,
            UpdatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
    return Results.Created($"/payments/{payment.Id}", new { paymentIntentId = payment.Id, status = payment.Status, clientSecret = payment.ClientSecret });
});

app.MapGet("/payments/{id}", async (string id, PaymentsDbContext db, CancellationToken ct) =>
    await db.AchPayments.FindAsync([id], ct) is { } p ? Results.Ok(p) : Results.NotFound());

app.MapPost("/webhooks/stripe", async (HttpRequest http, IWebhookSignatureVerifier verifier,
    WebhookProcessor processor, CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Body);
    var payload = await reader.ReadToEndAsync(ct);

    if (!verifier.IsValid(payload, http.Headers["Stripe-Signature"].FirstOrDefault()))
        return Results.BadRequest(new { error = "Invalid signature." });

    var result = await processor.ProcessAsync(payload, ct);
    // Always 200 for a verified event (including duplicates) so Stripe stops retrying.
    return Results.Ok(new { result = result.ToString() });
});

app.Run();

public record CreateCustomerRequest(string Email, string? Name);
public record CreateSubscriptionRequest(string CustomerId, string PriceId);
public record CreateAchPaymentRequest(string CustomerId, long AmountCents);

public partial class Program;
