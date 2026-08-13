using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WeChatBot.Backend.Infrastructure;

public sealed class DomainException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static DomainException NotFound(string resource) =>
        new(StatusCodes.Status404NotFound, "resource_not_found", $"{resource} was not found.");

    public static DomainException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    public static DomainException Validation(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    /// <summary>创建表示调用方已认证但无权使用目标身份或资源的 403 领域异常。</summary>
    /// <param name="code">稳定、可由客户端处理的错误码。</param>
    /// <param name="message">不泄露敏感注册状态的错误说明。</param>
    /// <returns>由统一异常处理器转换为 HTTP 403 的异常。</returns>
    public static DomainException Forbidden(string code, string message) =>
        new(StatusCodes.Status403Forbidden, code, message);
}

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, code, title, detail) = exception switch
        {
            DomainException domain => (domain.StatusCode, domain.Code, "Request could not be completed", domain.Message),
            SqliteException { SqliteErrorCode: 5 or 6 } => (
                StatusCodes.Status409Conflict,
                "database_write_busy",
                "Database write contention",
                "The database is temporarily busy with another write; retry the request."),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict", "Concurrent update detected", "The resource changed after it was read. Reload it and retry."),
            DbUpdateException => (StatusCodes.Status409Conflict, "database_conflict", "Database constraint conflict", "The request conflicts with an existing record."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Unexpected server error", "An unexpected error occurred.")
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled request failure. TraceId: {TraceId}", httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogWarning(exception, "Rejected request. Code: {Code}; TraceId: {TraceId}", code, httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions =
                {
                    ["errorCode"] = code,
                    ["traceId"] = httpContext.TraceIdentifier
                }
            },
            Exception = exception
        });
    }
}
