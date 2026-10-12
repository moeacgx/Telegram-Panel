using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class AdminLoginLimiterTests
{
    [Fact]
    public void FailedAttemptsSharePerAddressAndGlobalWindows()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var limiter = new AdminLoginLimiter(clock);

        Assert.True(limiter.TryAcquire("198.51.100.1", out _));
        Assert.True(limiter.TryAcquire("198.51.100.1", out _));
        Assert.True(limiter.TryAcquire("198.51.100.1", out _));
        Assert.True(limiter.TryAcquire("198.51.100.1", out _));
        Assert.True(limiter.TryAcquire("198.51.100.1", out _));
        Assert.False(limiter.TryAcquire("198.51.100.1", out var retryAfter));
        Assert.InRange(retryAfter, 1, 60);
        Assert.True(limiter.TryAcquire("198.51.100.2", out _));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire("198.51.100.1", out _));
    }

    [Fact]
    public void GlobalWindowStopsDistributedAttempts()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var limiter = new AdminLoginLimiter(clock);
        for (var i = 0; i < 30; i++)
            Assert.True(limiter.TryAcquire($"198.51.100.{i + 1}", out _));
        Assert.False(limiter.TryAcquire("203.0.113.1", out _));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
