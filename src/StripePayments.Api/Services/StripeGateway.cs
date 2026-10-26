using Stripe;

namespace StripePayments.Api.Services;

public class StripeGateway(StripeClient client) : IStripeGateway
{
    public async Task<CreatedCustomer> CreateCustomerAsync(string email, string? name, string idempotencyKey, CancellationToken ct)
    {
        var svc = new CustomerService(client);
        var customer = await svc.CreateAsync(
            new CustomerCreateOptions { Email = email, Name = name },
            new RequestOptions { IdempotencyKey = idempotencyKey }, ct);
        return new CreatedCustomer(customer.Id);
    }

    public async Task<CreatedSubscription> CreateSubscriptionAsync(string customerId, string priceId, string idempotencyKey, CancellationToken ct)
    {
        var svc = new SubscriptionService(client);
        var sub = await svc.CreateAsync(new SubscriptionCreateOptions
        {
            Customer = customerId,
            Items = [new SubscriptionItemOptions { Price = priceId }],
            PaymentBehavior = "default_incomplete",
        }, new RequestOptions { IdempotencyKey = idempotencyKey }, ct);
        return new CreatedSubscription(sub.Id, sub.Status, sub.LatestInvoiceId);
    }

    public async Task<CreatedAchPayment> CreateAchPaymentAsync(string customerId, long amountCents, string idempotencyKey, CancellationToken ct)
    {
        var svc = new PaymentIntentService(client);
        var intent = await svc.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = amountCents,
            Currency = "usd",
            Customer = customerId,
            // Limit this intent to ACH bank debit (US bank account). The typed option was removed in recent SDK versions,
            // so the raw API parameter is passed through ExtraParams.
            ExtraParams = new Dictionary<string, object> { ["payment_method_types[0]"] = "us_bank_account" },
        }, new RequestOptions { IdempotencyKey = idempotencyKey }, ct);
        return new CreatedAchPayment(intent.Id, intent.Status, intent.ClientSecret);
    }
}
