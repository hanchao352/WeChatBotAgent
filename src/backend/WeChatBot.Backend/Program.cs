using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

var builder = WebApplication.CreateBuilder(args);
var localEnvironment = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

ApplyAndValidateSecrets(builder.Configuration, localEnvironment);
ValidateRemarkTaskLeaseConfiguration(builder.Configuration);
EnsureSqliteDirectory(builder.Configuration.GetConnectionString("Database"));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<ActivationOptions>(builder.Configuration.GetSection("Activation"));
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection("Audit"));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
// 备注任务租约使用独立配置，避免把执行恢复时间隐式绑定到心跳或数据库超时。
builder.Services.Configure<RemarkTaskLeaseOptions>(builder.Configuration.GetSection("RemarkTaskLease"));
// 游标保护使用独立秘密，避免与鉴权、激活码、审计或备份密钥复用。
builder.Services.Configure<CursorPaginationOptions>(builder.Configuration.GetSection("Pagination"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Database")));

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.AuthenticationSchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.AuthenticationSchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Request validation failed"
        };
        problem.Extensions["errorCode"] = "validation_failed";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    };
});

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "WeChatBot commercial core API",
        Version = "v1",
        Description = "Backend orchestration API. It does not claim or simulate successful WeChat UI Automation execution."
    });
    options.AddSecurityDefinition(ApiKeyAuthenticationHandler.AuthenticationSchemeName, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyAuthenticationHandler.HeaderName,
        Description = "Server-validated administrative API key."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.AuthenticationSchemeName, document, null)] = []
    });
});

builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<ActivationCodeHasher>();
builder.Services.AddScoped<ActivationService>();
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddScoped<RemarkService>();
builder.Services.AddScoped<LogicalBackupService>();
builder.Services.AddScoped<AgentControlService>();
// 所有 Agent 业务端点共享同一个 claim/路由/正文/数据库绑定校验器，防止单个端点遗漏身份约束。
builder.Services.AddScoped<AgentIdentityBindingService>();
// 状态转换钩子默认不执行额外逻辑；集成测试替换它以稳定停在身份复核与提交之间，验证凭据竞态。
builder.Services.AddScoped<IAgentMutationSynchronization, NoOpAgentMutationSynchronization>();
builder.Services.AddScoped<IRemarkTaskMutationSynchronization, NoOpRemarkTaskMutationSynchronization>();
builder.Services.AddScoped<RemarkTaskLeaseService>();
// 保护器不保存请求或租户状态，可作为单例复用；租户绑定信息在每次保护或解析时显式传入。
builder.Services.AddSingleton<CursorProtector>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    if (response.HasStarted || response.ContentLength is not null || !string.IsNullOrEmpty(response.ContentType)) return;
    var title = response.StatusCode switch
    {
        StatusCodes.Status401Unauthorized => "Authentication required",
        StatusCodes.Status403Forbidden => "Access denied",
        StatusCodes.Status404NotFound => "Resource not found",
        _ => "Request failed"
    };
    await response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = response.StatusCode,
        Title = title,
        Extensions =
        {
            ["errorCode"] = response.StatusCode switch
            {
                StatusCodes.Status401Unauthorized => "unauthorized",
                StatusCodes.Status403Forbidden => "forbidden",
                StatusCodes.Status404NotFound => "route_not_found",
                _ => "request_failed"
            },
            ["traceId"] = statusCodeContext.HttpContext.TraceIdentifier
        }
    }, cancellationToken: statusCodeContext.HttpContext.RequestAborted);
});
if (localEnvironment)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
if (!localEnvironment)
{
    app.Map("/swagger/{**path}", () => Results.NotFound()).AllowAnonymous();
}

await DbInitializer.InitializeAsync(app.Services, localEnvironment);
await app.RunAsync();

/// <summary>
/// 验证备注任务租约时长处于服务允许范围内，防止错误配置造成永久独占或高频续租。
/// </summary>
/// <param name="configuration">应用配置来源。</param>
static void ValidateRemarkTaskLeaseConfiguration(IConfiguration configuration)
{
    const int minimumLeaseSeconds = 15;
    const int maximumLeaseSeconds = 300;
    var configured = configuration["RemarkTaskLease:DurationSeconds"];
    if (!int.TryParse(configured, out var durationSeconds) ||
        durationSeconds is < minimumLeaseSeconds or > maximumLeaseSeconds)
    {
        throw new InvalidOperationException(
            $"RemarkTaskLease__DurationSeconds must be between {minimumLeaseSeconds} and {maximumLeaseSeconds}.");
    }
}

static void ApplyAndValidateSecrets(IConfiguration configuration, bool localEnvironment)
{
    if (localEnvironment)
    {
        configuration["Auth:ApiKey"] = EmptyOrExisting(configuration["Auth:ApiKey"], "wechatbot-local-development-key-change-me");
        configuration["Auth:AgentApiKey"] = EmptyOrExisting(configuration["Auth:AgentApiKey"], "wechatbot-local-agent-development-key-change-me");
        configuration["Activation:HashPepper"] = EmptyOrExisting(configuration["Activation:HashPepper"], "local-activation-pepper-change-before-production-2026");
        configuration["Audit:IntegrityKey"] = EmptyOrExisting(configuration["Audit:IntegrityKey"], "local-audit-integrity-key-change-before-production-2026");
        configuration["Pagination:ProtectionKey"] = EmptyOrExisting(
            configuration["Pagination:ProtectionKey"],
            "local-cursor-protection-key-change-before-production-2026");
        configuration["Backup:EncryptionKeyBase64"] = EmptyOrExisting(
            configuration["Backup:EncryptionKeyBase64"],
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("local-backup-key-change-before-production"))));
        configuration["Auth:AllowAgentAutoRegistration"] = EmptyOrExisting(
            configuration["Auth:AllowAgentAutoRegistration"],
            "true");
        configuration["Auth:AllowLegacySharedAgentApiKey"] = EmptyOrExisting(
            configuration["Auth:AllowLegacySharedAgentApiKey"],
            "false");
    }

    RequireSecret(configuration["Auth:ApiKey"], "Auth__ApiKey", 32);
    if (!bool.TryParse(
            configuration["Auth:AllowLegacySharedAgentApiKey"],
            out var allowLegacySharedAgentApiKey))
    {
        throw new InvalidOperationException(
            "Auth__AllowLegacySharedAgentApiKey must be explicitly set to true or false.");
    }
    if (allowLegacySharedAgentApiKey)
    {
        RequireSecret(configuration["Auth:AgentApiKey"], "Auth__AgentApiKey", 32);
        if (string.Equals(configuration["Auth:ApiKey"], configuration["Auth:AgentApiKey"], StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Auth__ApiKey and Auth__AgentApiKey must be different secrets.");
        }
    }
    RequireSecret(configuration["Activation:HashPepper"], "Activation__HashPepper", 32);
    RequireSecret(configuration["Audit:IntegrityKey"], "Audit__IntegrityKey", 32);
    RequireSecret(configuration["Pagination:ProtectionKey"], "Pagination__ProtectionKey", 32);
    RequireActor(configuration["Auth:ActorName"], "Auth__ActorName");
    RequireActor(configuration["Auth:AgentActorName"], "Auth__AgentActorName");
    if (string.Equals(
            configuration["Auth:ActorName"]?.Trim(),
            configuration["Auth:AgentActorName"]?.Trim(),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Auth__ActorName and Auth__AgentActorName must identify different actors.");
    }
    var backupKey = configuration["Backup:EncryptionKeyBase64"];
    try
    {
        if (string.IsNullOrWhiteSpace(backupKey) || Convert.FromBase64String(backupKey).Length != 32) throw new FormatException();
    }
    catch (FormatException)
    {
        throw new InvalidOperationException("Backup__EncryptionKeyBase64 must contain a base64-encoded 32-byte key.");
    }
    if (!Guid.TryParse(configuration["Auth:TenantId"], out var tenantId) || tenantId == Guid.Empty)
        throw new InvalidOperationException("Auth__TenantId must contain a non-empty GUID.");
    if (!localEnvironment)
    {
        if (allowLegacySharedAgentApiKey)
        {
            throw new InvalidOperationException(
                "Production Auth__AllowLegacySharedAgentApiKey must be explicitly set to false; use per-Agent credentials.");
        }
        if (tenantId == Guid.Parse("11111111-1111-1111-1111-111111111111") ||
            string.Equals(configuration["Auth:ActorName"]?.Trim(), "local-admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["Auth:AgentActorName"]?.Trim(), "local-agent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production must override the development tenant and actor identities.");
        }
        RejectDevelopmentSecret(
            configuration["Auth:ApiKey"],
            "wechatbot-local-development-key-change-me",
            "Auth__ApiKey");
        // 即使兼容开关关闭，也拒绝在生产配置中遗留公开共享密钥，避免后续误开开关即暴露。
        RejectDevelopmentSecret(
            configuration["Auth:AgentApiKey"],
            "wechatbot-local-agent-development-key-change-me",
            "Auth__AgentApiKey");
        RejectDevelopmentSecret(
            configuration["Activation:HashPepper"],
            "local-activation-pepper-change-before-production-2026",
            "Activation__HashPepper");
        RejectDevelopmentSecret(
            configuration["Audit:IntegrityKey"],
            "local-audit-integrity-key-change-before-production-2026",
            "Audit__IntegrityKey");
        RejectDevelopmentSecret(
            configuration["Pagination:ProtectionKey"],
            "local-cursor-protection-key-change-before-production-2026",
            "Pagination__ProtectionKey");
        RejectDevelopmentSecret(
            configuration["Backup:EncryptionKeyBase64"],
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("local-backup-key-change-before-production"))),
            "Backup__EncryptionKeyBase64");
        if (!bool.TryParse(configuration["Auth:AllowAgentAutoRegistration"], out var allowAgentAutoRegistration) ||
            allowAgentAutoRegistration)
        {
            throw new InvalidOperationException(
                "Production Auth__AllowAgentAutoRegistration must be explicitly set to false.");
        }
        RequireAbsoluteSqlitePath(configuration.GetConnectionString("Database"));
        RequireAbsoluteDirectory(configuration["Backup:Directory"], "Backup__Directory");
    }
}

static string EmptyOrExisting(string? current, string fallback) => string.IsNullOrWhiteSpace(current) ? fallback : current;

static void RequireSecret(string? value, string environmentVariable, int minimumLength)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length < minimumLength)
        throw new InvalidOperationException($"{environmentVariable} is required and must be at least {minimumLength} characters.");
}

static void RequireActor(string? value, string environmentVariable)
{
    if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 || value.Contains('|', StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"{environmentVariable} is required, must be at most 128 characters, and cannot contain '|'.");
    }
}

static void RejectDevelopmentSecret(string? value, string developmentValue, string environmentVariable)
{
    if (string.Equals(value, developmentValue, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Production {environmentVariable} must not use the public development credential.");
    }
}

static void EnsureSqliteDirectory(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings__Database is required.");
    var parsed = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(parsed.DataSource) || parsed.DataSource == ":memory:") return;
    var fullPath = Path.GetFullPath(parsed.DataSource);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
}

static void RequireAbsoluteSqlitePath(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("ConnectionStrings__Database is required.");
    var parsed = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(parsed.DataSource) || !Path.IsPathFullyQualified(parsed.DataSource))
    {
        throw new InvalidOperationException(
            "Production ConnectionStrings__Database must use an absolute SQLite data source path.");
    }
}

static void RequireAbsoluteDirectory(string? path, string environmentVariable)
{
    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        throw new InvalidOperationException($"Production {environmentVariable} must be an absolute path.");
}

/// <summary>为 ASP.NET Core 入口提供集成测试可发现的公开类型。</summary>
public partial class Program;
