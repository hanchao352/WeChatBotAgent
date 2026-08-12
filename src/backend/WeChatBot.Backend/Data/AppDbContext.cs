using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Data;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    TenantContext tenantContext,
    IOptions<AuthOptions> authOptions)
    : DbContext(options)
{
    public DbSet<TenantState> Tenants => Set<TenantState>();
    public DbSet<AgentRegistration> AgentRegistrations => Set<AgentRegistration>();
    public DbSet<AgentHeartbeatState> AgentHeartbeatStates => Set<AgentHeartbeatState>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<GroupChat> Groups => Set<GroupChat>();
    public DbSet<RemarkRule> RemarkRules => Set<RemarkRule>();
    public DbSet<RemarkTask> RemarkTasks => Set<RemarkTask>();
    public DbSet<GroupMentionEvent> GroupMentions => Set<GroupMentionEvent>();
    public DbSet<ServicePackage> ServicePackages => Set<ServicePackage>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<EntitlementLedger> EntitlementLedger => Set<EntitlementLedger>();
    public DbSet<ActivationCode> ActivationCodes => Set<ActivationCode>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<BackupManifest> BackupManifests => Set<BackupManifest>();
    public DbSet<RestoreOperation> RestoreOperations => Set<RestoreOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TenantState>(entity =>
        {
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AgentRegistration>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.NormalizedAgentId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WeChatInstanceId }).IsUnique();
            entity.Property(x => x.AgentId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.NormalizedAgentId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.WeChatInstanceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ConfigurationVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AgentHeartbeatState>(entity =>
        {
            entity.HasKey(x => x.AgentRegistrationId);
            entity.HasIndex(x => new { x.TenantId, x.ReceivedAt });
            entity.Property(x => x.RuntimeState).HasConversion<string>().HasMaxLength(48);
            entity.Property(x => x.ReasonCode).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.LastCommandCode).HasMaxLength(128);
            entity.Property(x => x.AgentVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LastRejectedWeChatInstanceId).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<AgentRegistration>()
                .WithOne()
                .HasForeignKey<AgentHeartbeatState>(x => x.AgentRegistrationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Contact>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ExternalId }).IsUnique();
            entity.Property(x => x.ExternalId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.WeChatId).HasMaxLength(128);
            entity.Property(x => x.CustomerCode).HasMaxLength(128);
            entity.Property(x => x.SystemRemark).HasMaxLength(256);
            entity.Property(x => x.CurrentWeChatRemark).HasMaxLength(256);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GroupChat>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ExternalId }).IsUnique();
            entity.Property(x => x.ExternalId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.BusinessName).HasMaxLength(256);
            entity.Property(x => x.SystemRemark).HasMaxLength(256);
            entity.Property(x => x.CurrentWeChatRemark).HasMaxLength(256);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<RemarkRule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Template).HasMaxLength(512).IsRequired();
            entity.Property(x => x.TargetKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ConflictPolicy).HasConversion<string>().HasMaxLength(48);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<RemarkTask>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.GeneratedRemark).HasMaxLength(256).IsRequired();
            entity.Property(x => x.TargetKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<RemarkRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<GroupMentionEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ExternalEventId }).IsUnique();
            entity.Property(x => x.ExternalEventId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.SenderExternalId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Content).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.Decision).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<GroupChat>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ServicePackage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Tier).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.FeaturesJson).IsRequired();
        });

        modelBuilder.Entity<Entitlement>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.TargetKind, x.TargetId });
            entity.HasIndex(x => x.ActivationCodeId).IsUnique();
            entity.Property(x => x.TargetKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.DurationKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Source).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<ServicePackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<EntitlementLedger>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EntitlementId, x.OccurredAt });
            entity.Property(x => x.EventType).HasMaxLength(48).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(160).IsRequired();
            entity.HasOne<Entitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ActivationCode>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DurationKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RedeemedTargetKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RevokedBy).HasMaxLength(160);
            entity.Property(x => x.RevocationReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<ServicePackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Operation, x.Key }).IsUnique();
            entity.Property(x => x.Operation).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt });
            entity.Property(x => x.Actor).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ResourceType).HasMaxLength(96).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IntegrityHash).HasMaxLength(64).IsRequired();
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<BackupManifest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt });
            entity.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.PayloadSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<RestoreOperation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasOne<BackupManifest>().WithMany().HasForeignKey(x => x.BackupManifestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        if (Database.IsSqlite())
        {
            var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
                value => value.UtcDateTime.Ticks,
                value => new DateTimeOffset(value, TimeSpan.Zero));
            var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
                value => value.HasValue ? value.Value.UtcDateTime.Ticks : null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(dateTimeOffsetConverter);
                    if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceImmutableRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceImmutableRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceImmutableRecords()
    {
        var expectedTenantId = tenantContext.TenantId == Guid.Empty
            ? authOptions.Value.TenantId
            : tenantContext.TenantId;
        var invalidTenantEntries = ChangeTracker.Entries<ITenantEntity>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                        (expectedTenantId == Guid.Empty ||
                         x.Entity.TenantId != expectedTenantId ||
                         x.State is EntityState.Modified or EntityState.Deleted &&
                         x.Property(entity => entity.TenantId).OriginalValue != expectedTenantId))
            .ToArray();
        if (invalidTenantEntries.Length > 0)
        {
            throw new InvalidOperationException("Tenant-scoped records must match the authenticated tenant.");
        }

        var immutableChanges = ChangeTracker.Entries()
            .Where(x => (x.Entity is AuditLog || x.Entity is EntitlementLedger) &&
                        x.State is EntityState.Modified or EntityState.Deleted)
            .ToArray();

        if (immutableChanges.Length > 0)
        {
            throw new InvalidOperationException("Audit and entitlement ledger records are append-only.");
        }
    }
}
