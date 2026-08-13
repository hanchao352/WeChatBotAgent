using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Tests;

/// <summary>
/// 为并发集成测试提供一次性阻塞同步点，使请求稳定停在 Agent 身份复核之后、业务事务提交之前。
/// </summary>
internal sealed class BlockingAgentMutationSynchronization(string expectedOperation)
    : IAgentMutationSynchronization
{
    /// <summary>保存目标操作名称，非目标操作不阻塞。</summary>
    private readonly string _expectedOperation = expectedOperation;

    /// <summary>在目标操作到达同步点时完成，供测试启动竞争请求。</summary>
    private readonly TaskCompletionSource _reached = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>由测试完成以允许被阻塞请求继续提交。</summary>
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>目标操作到达后通知测试并等待显式放行；其他操作立即返回。</summary>
    /// <param name="operation">当前 Agent 状态转换的稳定操作名。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    public async Task AfterBindingValidatedAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(operation, _expectedOperation, StringComparison.Ordinal)) return;

        _reached.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }

    /// <summary>等待目标请求到达同步点，并以有限超时防止失败测试永久挂起。</summary>
    /// <param name="timeout">允许请求到达同步点的最长时间。</param>
    public Task WaitUntilReachedAsync(TimeSpan timeout) => _reached.Task.WaitAsync(timeout);

    /// <summary>放行目标请求继续执行并提交业务事务。</summary>
    public void Release() => _released.TrySetResult();
}
