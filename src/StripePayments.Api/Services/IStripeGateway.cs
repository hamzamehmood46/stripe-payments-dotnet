namespace StripePayments.Api.Services;

public record CreatedCustomer(string Id);
public record CreatedSubscription(string Id, string Status, string? LatestInvoiceId);
public record CreatedAchPayment(string Id, string Status, string? ClientSecret);

/// <summary>Thin seam over Stripe.net so the rest of the app (and the tests) never touch the SDK directly.</summary>
public interface IStripeGateway
{
    Task<CreatedCustomer> CreateCustomerAsync(string email, string? name, string idempotencyKey, CancellationToken ct);
    Task<CreatedSubscription> CreateSubscriptionAsync(string customerId, string priceId, string idempotencyKey, CancellationToken ct);
    Task<CreatedAchPayment> CreateAchPaymentAsync(string customerId, long amountCents, string idempotencyKey, CancellationToken ct);
}
