using WeChatBot.Agent.Runtime;

namespace WeChatBot.Agent.Tests;

public sealed class WeChatInstanceLeaseTests
{
    [Fact]
    public void StorageKeyIsStableDistinctAndDoesNotExposeInstanceId()
    {
        const string firstId = "tenant-private-instance";

        var first = WeChatInstanceIdentity.ToStorageKey(firstId);
        var repeated = WeChatInstanceIdentity.ToStorageKey(firstId);
        var second = WeChatInstanceIdentity.ToStorageKey("another-instance");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(firstId, first, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondThreadCannotAcquireSameInstanceUntilLeaseIsReleased()
    {
        var instanceId = $"test-{Guid.NewGuid():N}";
        Assert.True(WeChatInstanceLease.TryAcquire(instanceId, out var first));
        Assert.NotNull(first);
        WeChatInstanceLease? second = null;
        var secondAcquired = true;
        var competingThread = new Thread(() =>
        {
            secondAcquired = WeChatInstanceLease.TryAcquire(instanceId, out second);
        });

        try
        {
            competingThread.Start();
            Assert.True(competingThread.Join(TimeSpan.FromSeconds(5)));
            Assert.False(secondAcquired);
            Assert.Null(second);
        }
        finally
        {
            second?.Dispose();
            first!.Dispose();
        }
    }

    [Fact]
    public async Task LeaseCanBeReleasedAfterAsyncThreadHop()
    {
        var instanceId = $"test-{Guid.NewGuid():N}";
        Assert.True(WeChatInstanceLease.TryAcquire(instanceId, out var first));

        await Task.Yield();
        first!.Dispose();

        Assert.True(WeChatInstanceLease.TryAcquire(instanceId, out var second));
        second!.Dispose();
    }
}
