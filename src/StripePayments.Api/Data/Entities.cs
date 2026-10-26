namespace StripePayments.Api.Data;

public class StripeCustomer
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string? Name { get; set; }
    public string StripeCustomerId { get; set; } = "";
}

public class SubscriptionRecord
{
    public string StripeSubscriptionId { get; set; } = "";
    public string StripeCustomerId { get; set; } = "";
    public string PriceId { get; set; } = "";
    public string Status { get; set; } = "incomplete";
    public DateTime UpdatedUtc { get; set; }
}

public class AchPayment
{
    public string PaymentIntentId { get; set; } = "";
    public string StripeCustomerId { get; set; } = "";
    public long AmountCents { get; set; }
    public string Status { get; set; } = "requires_payment_method";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One row per Stripe event we have handled. The primary key is what makes webhook handling idempotent.</summary>
public class ProcessedEvent
{
    public string EventId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ProcessedUtc { get; set; }
}
