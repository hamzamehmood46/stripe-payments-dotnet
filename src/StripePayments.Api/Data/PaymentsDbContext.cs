using Microsoft.EntityFrameworkCore;

namespace StripePayments.Api.Data;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<StripeCustomer> Customers => Set<StripeCustomer>();
    public DbSet<SubscriptionRecord> Subscriptions => Set<SubscriptionRecord>();
    public DbSet<AchPayment> AchPayments => Set<AchPayment>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<StripeCustomer>().HasIndex(c => c.StripeCustomerId).IsUnique();
        b.Entity<SubscriptionRecord>().HasKey(s => s.StripeSubscriptionId);
        b.Entity<AchPayment>().HasKey(p => p.PaymentIntentId);
        b.Entity<ProcessedEvent>().HasKey(e => e.EventId);
    }
}
