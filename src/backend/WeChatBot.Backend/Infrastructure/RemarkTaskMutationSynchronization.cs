namespace WeChatBot.Backend.Infrastructure;

/// <summary>定义备注任务状态转换在写锁内完成前置校验后的测试观察点。</summary>
public interface IRemarkTaskMutationSynchronization
{
    /// <summary>通知测试目标操作已完成前置校验，并等待测试允许事务继续。</summary>
    /// <param name="operation">稳定的备注任务操作名称。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    Task AfterStateValidatedAsync(string operation, CancellationToken cancellationToken);
}

/// <summary>生产默认实现，不阻塞或改变备注任务状态转换时序。</summary>
public sealed class NoOpRemarkTaskMutationSynchronization : IRemarkTaskMutationSynchronization
{
    /// <summary>直接返回已完成任务。</summary>
    /// <param name="operation">未使用的操作名称。</param>
    /// <param name="cancellationToken">未使用的请求取消令牌。</param>
    public Task AfterStateValidatedAsync(string operation, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
