using Stripe;

namespace StripePayments.Api.Services;

public interface IWebhookSignatureVerifier
{
    /// <summary>Returns true when the Stripe-Signature header is valid for this exact payload.</summary>
    bool IsValid(string payload, string? signatureHeader);
}

public class WebhookSignatureVerifier(string webhookSecret, int toleranceSeconds = 300) : IWebhookSignatureVerifier
{
    public bool IsValid(string payload, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(webhookSecret)) return false;
        try
        {
            // ConstructEvent checks the HMAC-SHA256 signature and the timestamp tolerance (replay protection).
            EventUtility.ConstructEvent(payload, signatureHeader, webhookSecret, toleranceSeconds, throwOnApiVersionMismatch: false);
            return true;
        }
        catch (StripeException)
        {
            return false;
        }
    }
}
