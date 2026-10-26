using System.Security.Cryptography;
using System.Text;
using StripePayments.Api.Services;

namespace StripePayments.Tests;

public class WebhookSignatureTests
{
    private const string Secret = "whsec_test_secret";
    private const string Payload = """{"id":"evt_1","object":"event","type":"invoice.paid","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":null,"idempotency_key":null},"data":{"object":{"id":"in_1"}}}""";

    private static string Sign(string payload, string secret, long timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
        return $"t={timestamp},v1={hash}";
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void A_correctly_signed_payload_is_accepted()
    {
        var verifier = new WebhookSignatureVerifier(Secret);
        Assert.True(verifier.IsValid(Payload, Sign(Payload, Secret, Now())));
    }

    [Fact]
    public void A_payload_signed_with_the_wrong_secret_is_rejected()
    {
        var verifier = new WebhookSignatureVerifier(Secret);
        Assert.False(verifier.IsValid(Payload, Sign(Payload, "whsec_other", Now())));
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        var verifier = new WebhookSignatureVerifier(Secret);
        var header = Sign(Payload, Secret, Now());
        Assert.False(verifier.IsValid(Payload.Replace("in_1", "in_2"), header));
    }

    [Fact]
    public void An_old_signature_is_rejected_to_block_replays()
    {
        var verifier = new WebhookSignatureVerifier(Secret, toleranceSeconds: 300);
        var tenMinutesAgo = Now() - 600;
        Assert.False(verifier.IsValid(Payload, Sign(Payload, Secret, tenMinutesAgo)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-signature")]
    public void A_missing_or_malformed_header_is_rejected(string? header)
    {
        var verifier = new WebhookSignatureVerifier(Secret);
        Assert.False(verifier.IsValid(Payload, header));
    }
}
