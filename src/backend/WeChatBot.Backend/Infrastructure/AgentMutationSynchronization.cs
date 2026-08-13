namespace WeChatBot.Backend.Infrastructure;

/// <summary>
/// 定义 Agent 状态转换在取得数据库写锁并完成身份复核后的同步观察点。
/// 生产实现不阻塞；测试实现可稳定协调并发凭据轮换或吊销，验证事务边界不存在校验后写入窗口。
/// </summary>
public interface IAgentMutationSynchronization
{
    /// <summary>
    /// 在写锁内的身份复核完成后通知观察者，并等待其允许状态转换继续。
    /// </summary>
    /// <param name="operation">稳定的操作名称，例如 remark-task.claim 或 group-mention.ingest。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    Task AfterBindingValidatedAsync(string operation, CancellationToken cancellationToken);
}

/// <summary>
/// 生产默认同步实现；不分配额外任务对象，也不会改变请求时序。
/// </summary>
public sealed class NoOpAgentMutationSynchronization : IAgentMutationSynchronization
{
    /// <summary>直接返回已完成任务，保持生产关键路径无额外堆分配和等待。</summary>
    /// <param name="operation">未使用的稳定操作名称。</param>
    /// <param name="cancellationToken">未使用的请求取消令牌。</param>
    public Task AfterBindingValidatedAsync(string operation, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
