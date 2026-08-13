namespace WeChatBot.Backend.Tests;

/// <summary>在请求交给 TestServer 前通知测试，避免用固定延时猜测请求是否已经发出。</summary>
internal sealed class RequestStartedHandler(TaskCompletionSource started) : DelegatingHandler
{
    /// <summary>记录请求已进入 HTTP 管道，然后交给 TestServer 继续处理。</summary>
    /// <param name="request">待发送的 HTTP 请求。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>TestServer 生成的 HTTP 响应任务。</returns>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        started.TrySetResult();
        return base.SendAsync(request, cancellationToken);
    }
}
