using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Tests;

public sealed class DurationTests
{
    [Theory]
    [InlineData(ServiceDurationKind.Days30, 30)]
    [InlineData(ServiceDurationKind.Days60, 60)]
    [InlineData(ServiceDurationKind.Days90, 90)]
    public void Fixed_day_durations_use_exact_half_open_boundaries(ServiceDurationKind duration, int days)
    {
        var start = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.FromHours(8));
        var end = ServiceDurationCalculator.CalculateEnd(start, duration);

        Assert.Equal(start.AddDays(days), end);
        var entitlement = BuildEntitlement(start, end);
        Assert.True(EntitlementEvaluator.IsActive(entitlement, start));
        Assert.True(EntitlementEvaluator.IsActive(entitlement, end!.Value.AddTicks(-1)));
        Assert.False(EntitlementEvaluator.IsActive(entitlement, end.Value));
    }

    [Fact]
    public void Half_year_uses_calendar_months_and_preserves_offset()
    {
        var start = new DateTimeOffset(2024, 2, 29, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(new DateTimeOffset(2024, 8, 29, 10, 0, 0, TimeSpan.FromHours(8)),
            ServiceDurationCalculator.CalculateEnd(start, ServiceDurationKind.HalfYear));
    }

    [Fact]
    public void One_year_handles_leap_day_as_calendar_year()
    {
        var start = new DateTimeOffset(2024, 2, 29, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(new DateTimeOffset(2025, 2, 28, 10, 0, 0, TimeSpan.FromHours(8)),
            ServiceDurationCalculator.CalculateEnd(start, ServiceDurationKind.OneYear));
    }

    [Fact]
    public void Permanent_entitlement_has_no_end()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(ServiceDurationCalculator.CalculateEnd(start, ServiceDurationKind.Permanent));
        Assert.True(EntitlementEvaluator.IsActive(BuildEntitlement(start, null), start.AddYears(100)));
    }

    private static Entitlement BuildEntitlement(DateTimeOffset start, DateTimeOffset? end) => new()
    {
        State = EntitlementState.Active,
        StartsAt = start,
        EndsAt = end
    };
}
