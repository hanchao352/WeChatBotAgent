using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Tests;

/// <summary>把管理员备注任务状态转换稳定阻塞在前置校验之后、事务提交之前。</summary>
internal sealed class BlockingRemarkTaskMutationSynchronization(
    string expectedOperation,
    int skipMatches = 0)
    : IRemarkTaskMutationSynchronization
{
    /// <summary>通知测试目标操作已经进入同步点。</summary>
    private readonly TaskCompletionSource _reached = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>由测试显式完成以允许目标事务继续。</summary>
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>构造种子数据时允许跳过的匹配调用次数。</summary>
    private int _remainingSkips = skipMatches;

    /// <summary>目标操作到达时通知测试并等待放行，其他操作立即返回。</summary>
    /// <param name="operation">当前备注任务操作名称。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    public async Task AfterStateValidatedAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(operation, expectedOperation, StringComparison.Ordinal)) return;
        if (Interlocked.Decrement(ref _remainingSkips) >= 0) return;
        _reached.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }

    /// <summary>以有限超时等待目标操作进入同步点。</summary>
    /// <param name="timeout">最长等待时间。</param>
    public Task WaitUntilReachedAsync(TimeSpan timeout) => _reached.Task.WaitAsync(timeout);

    /// <summary>允许被阻塞的事务继续提交。</summary>
    public void Release() => _released.TrySetResult();
}
