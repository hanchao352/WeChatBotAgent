namespace WeChatBot.Agent.Runtime;

public sealed class WeChatInstanceLease : IDisposable
{
    private readonly Thread _ownerThread;
    private readonly ManualResetEventSlim _releaseRequested;
    private int _disposed;

    private WeChatInstanceLease(Thread ownerThread, ManualResetEventSlim releaseRequested)
    {
        _ownerThread = ownerThread;
        _releaseRequested = releaseRequested;
    }

    public static bool TryAcquire(string weChatInstanceId, out WeChatInstanceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weChatInstanceId);
        var mutexName = $"Global\\WeChatBot.Agent.{WeChatInstanceIdentity.ToStorageKey(weChatInstanceId)}";
        using var ready = new ManualResetEventSlim(false);
        var releaseRequested = new ManualResetEventSlim(false);
        Exception? ownerFailure = null;
        var acquired = false;

        var ownerThread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, mutexName);
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                ready.Set();
                if (!acquired)
                {
                    return;
                }

                releaseRequested.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "WeChatBot instance lease"
        };

        ownerThread.Start();
        ready.Wait();

        if (ownerFailure is not null)
        {
            releaseRequested.Set();
            ownerThread.Join();
            releaseRequested.Dispose();
            throw new InvalidOperationException("Failed to acquire the WeChat instance lease.", ownerFailure);
        }

        if (!acquired)
        {
            ownerThread.Join();
            releaseRequested.Dispose();
            lease = null;
            return false;
        }

        lease = new WeChatInstanceLease(ownerThread, releaseRequested);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _releaseRequested.Set();
        _ownerThread.Join();
        _releaseRequested.Dispose();
    }
}
