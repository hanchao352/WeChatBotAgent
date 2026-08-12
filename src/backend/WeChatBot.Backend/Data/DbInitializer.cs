using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Data;

public static class SeedDataIds
{
    public static readonly Guid ContactId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc1");
    public static readonly Guid GroupId = Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd1");
    public static readonly Guid ContactRemarkRuleId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeee1");
    public static readonly Guid GroupRemarkRuleId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeee2");
}

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, bool seedDevelopmentData, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=30000;", cancellationToken);

        var now = clock.GetUtcNow();
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(x => x.TenantId == auth.TenantId, cancellationToken))
        {
            db.Tenants.Add(new TenantState
            {
                TenantId = auth.TenantId,
                Name = "Default tenant",
                AutomationPaused = false,
                UpdatedAt = now
            });
        }

        if (!await db.ServicePackages.AnyAsync(x => x.Id == WellKnownPackages.BasicId, cancellationToken))
        {
            db.ServicePackages.Add(new ServicePackage
            {
                Id = WellKnownPackages.BasicId,
                Code = "BASIC",
                Name = "基础服务",
                Tier = PackageTier.Basic,
                FeaturesJson = "[\"group-mention\",\"auto-remark\",\"standard-reply\"]",
                IsEnabled = true
            });
        }
        if (!await db.ServicePackages.AnyAsync(x => x.Id == WellKnownPackages.AdvancedGeneralId, cancellationToken))
        {
            db.ServicePackages.Add(new ServicePackage
            {
                Id = WellKnownPackages.AdvancedGeneralId,
                Code = "ADVANCED_GENERAL",
                Name = "高级通用服务",
                Tier = PackageTier.AdvancedGeneral,
                FeaturesJson = "[\"group-mention\",\"auto-remark\",\"standard-reply\",\"advanced-group-general\"]",
                IsEnabled = true
            });
        }

        if (seedDevelopmentData)
        {
            if (!await db.Contacts.IgnoreQueryFilters().AnyAsync(x => x.Id == SeedDataIds.ContactId, cancellationToken))
            {
                db.Contacts.Add(new Contact
                {
                    Id = SeedDataIds.ContactId,
                    TenantId = auth.TenantId,
                    ExternalId = "wx-contact-demo",
                    DisplayName = "示例联系人",
                    WeChatId = "demo_contact",
                    CustomerCode = "C10001",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            if (!await db.Groups.IgnoreQueryFilters().AnyAsync(x => x.Id == SeedDataIds.GroupId, cancellationToken))
            {
                db.Groups.Add(new GroupChat
                {
                    Id = SeedDataIds.GroupId,
                    TenantId = auth.TenantId,
                    ExternalId = "wx-group-demo",
                    DisplayName = "示例服务群",
                    BusinessName = "默认业务",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            if (!await db.RemarkRules.IgnoreQueryFilters().AnyAsync(x => x.Id == SeedDataIds.ContactRemarkRuleId, cancellationToken))
            {
                db.RemarkRules.Add(new RemarkRule
                {
                    Id = SeedDataIds.ContactRemarkRuleId,
                    TenantId = auth.TenantId,
                    Name = "联系人客户编号备注",
                    TargetKind = ServiceTargetKind.Contact,
                    Template = "{customerCode}-{displayName}",
                    ConflictPolicy = RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
                    IsEnabled = true,
                    MaxLength = 32,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            if (!await db.RemarkRules.IgnoreQueryFilters().AnyAsync(x => x.Id == SeedDataIds.GroupRemarkRuleId, cancellationToken))
            {
                db.RemarkRules.Add(new RemarkRule
                {
                    Id = SeedDataIds.GroupRemarkRuleId,
                    TenantId = auth.TenantId,
                    Name = "群业务备注",
                    TargetKind = ServiceTargetKind.Group,
                    Template = "{businessName}-{displayName}",
                    ConflictPolicy = RemarkConflictPolicy.OverwriteSystemGeneratedOnly,
                    IsEnabled = true,
                    MaxLength = 32,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
