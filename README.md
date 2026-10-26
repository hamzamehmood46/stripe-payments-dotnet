# Stripe Payments (.NET 8): subscriptions, ACH, and idempotent webhooks

A small ASP.NET Core API that integrates with **Stripe in test mode**: recurring subscriptions, ACH bank debit
(US bank account) payments, and a webhook endpoint that verifies Stripe's signature and processes each event exactly once.

**Live demo: [stripe-payments-dotnet.onrender.com](https://stripe-payments-dotnet.onrender.com/swagger)** (Swagger; Stripe is simulated there, so no keys and no money; the free host sleeps when idle, so the first request can take about a minute)

> **Test mode only.** The app refuses to start with anything other than an `sk_test_...` key, and no real money is involved.

## What it shows

| Concern | How it is handled |
|---|---|
| **Recurring payments** | `POST /subscriptions` creates a Stripe subscription (`default_incomplete`) for a customer and price. Invoice and subscription webhooks keep the local status in sync (`active`, `past_due`, `canceled`). |
| **ACH** | `POST /payments/ach` creates a `us_bank_account` PaymentIntent. `payment_intent.processing / succeeded / payment_failed` webhooks move it through `processing`, `succeeded` or `failed`. ACH settles asynchronously, so the webhook is the source of truth. |
| **Webhook security** | The `Stripe-Signature` header is verified with the signing secret before anything is parsed (HMAC-SHA256 plus a 5-minute timestamp tolerance to block replays). Invalid requests get `400`. |
| **Idempotent webhooks** | Each event id is stored in a `ProcessedEvent` table in the same transaction as its effects. A retried or replayed event returns `Duplicate` and changes nothing, even if two deliveries race. |
| **Idempotent requests** | Stripe calls carry an idempotency key (from the `Idempotency-Key` header, or derived). `POST /payments/ach` requires one, so a client retry cannot create a second charge. |
| **Testability** | Stripe.net is wrapped behind `IStripeGateway`; the webhook logic reads raw JSON, so tests need no network and no SDK object graphs. |

## Try the live demo (2 minutes)

1. `POST /customers` with an email, then `POST /subscriptions` with that customer id and any price id. The subscription starts as `incomplete`.
2. `POST /demo/simulate` with `{"type":"invoice.paid","objectId":"<the subscriptionId>"}`. The simulator builds a Stripe event, signs it like Stripe does and sends it through the same verification and processing as a real webhook. The subscription becomes `active`.
3. Send the same call with `"eventId":"evt_1"` twice: the second answer is `Duplicate` and nothing changes. Set `"tamper":true` or `"ageSeconds":3600` to see the signature check refuse it.
4. For ACH, `POST /payments/ach` with an `Idempotency-Key` header (repeat the call with the same key and you get the same payment, never a second charge), then simulate `payment_intent.processing` and `payment_intent.succeeded` for its id.

Demo mode is switched on with `Demo__Enabled=true` and replaces Stripe with an in-memory stand-in. Without it the app talks to Stripe test mode as described below.

## Run it with real Stripe test mode

```bash
cd src/StripePayments.Api
dotnet user-secrets init
dotnet user-secrets set "Stripe:SecretKey" "sk_test_..."        # from the Stripe dashboard (test mode)
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."      # printed by `stripe listen`
dotnet run
```

Forward Stripe test events to your machine with the [Stripe CLI](https://docs.stripe.com/stripe-cli):

```bash
stripe listen --forward-to http://localhost:5000/webhooks/stripe
stripe trigger invoice.paid
stripe trigger payment_intent.succeeded
```

### Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/customers` | Create a Stripe customer (`{ "email", "name" }`) |
| `POST` | `/subscriptions` | Start a recurring subscription (`{ "customerId", "priceId" }`) |
| `GET` | `/subscriptions/{id}` | Local subscription status |
| `POST` | `/payments/ach` | Create an ACH PaymentIntent (`{ "customerId", "amountCents" }`, `Idempotency-Key` header required) |
| `GET` | `/payments/{id}` | Local ACH payment status |
| `POST` | `/webhooks/stripe` | Signed Stripe webhook receiver |
| `GET` | `/health` | Liveness |

## Tests

```bash
dotnet test
```

33 tests (xUnit, SQLite) cover: invoice paid and failed transitions (including the newer nested invoice shape),
subscription created, updated and deleted, ACH processing, success and failure, unknown event types, an invoice for a subscription
we do not know about, **the same event delivered twice being applied once**, and signature checks (valid, wrong secret, tampered payload,
expired timestamp, missing or malformed header), plus the demo: the full subscription and ACH lifecycles through the simulator, duplicate delivery, tampered and expired events refused, same-key retries returning the same object, and the simulator not existing outside demo mode.

## Notes and limits

- Built against Stripe's **test mode**. It is a learning and portfolio project, not a production payment system.
- The ACH flow creates the PaymentIntent and tracks its lifecycle; collecting the bank account on the client (Stripe.js or Financial Connections) is out of scope.
- State is stored in SQLite for simplicity; swap the provider in `Program.cs` for SQL Server or PostgreSQL.
- No card data, bank details, or live keys are ever stored or logged by this app.
