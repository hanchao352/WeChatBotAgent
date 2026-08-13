using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace WeChatBot.Backend.Infrastructure;

/// <summary>
/// 定义游标分页的稳定 API 边界，避免各列表接口散落不同的默认值和容量上限。
/// </summary>
public static class CursorPaginationLimits
{
    /// <summary>允许请求的最小页容量。</summary>
    public const int MinimumPageSize = 1;

    /// <summary>未显式指定页容量时使用的默认值。</summary>
    public const int DefaultPageSize = 100;

    /// <summary>单次查询允许返回的最大页容量，用于限制数据库和响应内存压力。</summary>
    public const int MaximumPageSize = 500;

    /// <summary>
    /// 校验游标模式页容量并返回最终值；未提供时使用统一默认值。
    /// </summary>
    /// <param name="requestedPageSize">查询字符串中的可选页容量。</param>
    /// <returns>位于统一安全上下限内的页容量。</returns>
    /// <exception cref="DomainException">页容量超出允许范围时抛出。</exception>
    public static int Resolve(int? requestedPageSize)
    {
        // 空值表示客户端接受服务端默认容量；显式值必须同时满足下界和防滥用上界。
        var pageSize = requestedPageSize ?? DefaultPageSize;
        if (pageSize is < MinimumPageSize or > MaximumPageSize)
        {
            throw DomainException.Validation(
                "invalid_page_size",
                $"pageSize must be between {MinimumPageSize} and {MaximumPageSize}.");
        }

        return pageSize;
    }

    /// <summary>
    /// 校验旧版 <c>take</c> 参数，使兼容路径与游标路径共享相同容量边界。
    /// </summary>
    /// <param name="take">旧版数组响应请求的最大元素数量。</param>
    /// <returns>经过校验的旧版容量。</returns>
    /// <exception cref="DomainException">容量超出允许范围时抛出。</exception>
    public static int ValidateLegacyTake(int take)
    {
        if (take is < MinimumPageSize or > MaximumPageSize)
        {
            throw DomainException.Validation(
                "invalid_page_size",
                $"take must be between {MinimumPageSize} and {MaximumPageSize}.");
        }

        return take;
    }
}

/// <summary>
/// 保存游标保护所需的配置。保护密钥必须由环境或安全配置注入，不能写入业务代码或日志。
/// </summary>
public sealed class CursorPaginationOptions
{
    /// <summary>
    /// 获取或设置游标保护密钥；生产环境必须提供至少 32 个字符且不同于公开开发值的随机秘密。
    /// </summary>
    public string ProtectionKey { get; set; } = string.Empty;
}

/// <summary>
/// 表示从经过认证的游标中恢复出的键集位置。
/// </summary>
/// <param name="SortKey">上一页末项的主排序键，必须使用与数据库查询相同的比较规则。</param>
/// <param name="Id">上一页末项的唯一标识，用作主排序键相同时的确定性决胜键。</param>
public sealed record CursorPosition(string SortKey, Guid Id);

/// <summary>
/// 使用 AES-GCM 生成和解析不可预测、不可篡改且绑定租户及接口范围的不透明游标。
/// </summary>
public sealed class CursorProtector
{
    /// <summary>AES-GCM 标准随机数长度，单位为字节。</summary>
    private const int NonceSizeInBytes = 12;

    /// <summary>AES-GCM 认证标签长度，单位为字节。</summary>
    private const int AuthenticationTagSizeInBytes = 16;

    /// <summary>当前游标载荷结构版本。</summary>
    private const int CurrentPayloadVersion = 1;

    /// <summary>游标明文前缀，用于在解密前拒绝未知协议版本。</summary>
    private const string CurrentTokenPrefix = "v1.";

    /// <summary>允许接收的最大游标文本长度，防止畸形输入造成不受控的解码分配。</summary>
    private const int MaximumEncodedCursorLength = 4096;

    /// <summary>允许载荷携带的最大排序键长度，与当前联系人和群名称字段上限一致。</summary>
    private const int MaximumSortKeyLength = 256;

    /// <summary>作为附加认证数据参与 AES-GCM 校验的协议域分隔值。</summary>
    private static readonly byte[] AssociatedData = "wechatbot.cursor.v1"u8.ToArray();

    /// <summary>由配置秘密单向派生出的 256 位 AES 密钥，仅保存在服务进程内存中。</summary>
    private readonly byte[] _encryptionKey;

    /// <summary>
    /// 初始化游标保护器，并从配置秘密派生固定长度加密密钥。
    /// </summary>
    /// <param name="options">已经由启动流程完成必填和长度校验的游标配置。</param>
    public CursorProtector(IOptions<CursorPaginationOptions> options)
    {
        // SHA-256 仅用于把高熵配置秘密派生为 AES-256 所需的固定长度密钥；原始秘密不会进入游标。
        _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.ProtectionKey));
    }

    /// <summary>
    /// 为指定租户和列表范围保护一个键集位置。
    /// </summary>
    /// <param name="scope">标识排序语义的稳定接口范围，防止游标被用于其他资源或排序方式。</param>
    /// <param name="tenantId">当前认证租户标识，用于阻止游标跨租户重放。</param>
    /// <param name="position">上一页末项的复合排序位置。</param>
    /// <returns>可放入查询字符串的不透明 Base64Url 游标。</returns>
    public string Protect(string scope, Guid tenantId, CursorPosition position)
    {
        ValidateProtectionInput(scope, tenantId, position);

        // 载荷同时保存版本、作用域、租户、排序键和唯一 ID，完整描述下一页的严格起点。
        var payload = new CursorPayload(
            CurrentPayloadVersion,
            scope,
            tenantId,
            position.SortKey,
            position.Id);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var ciphertext = new byte[plaintext.Length];
        var authenticationTag = new byte[AuthenticationTagSizeInBytes];

        try
        {
            // 每次使用独立随机数加密，相同位置也会生成不同且不可预测的游标。
            using var aes = new AesGcm(_encryptionKey, AuthenticationTagSizeInBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, authenticationTag, AssociatedData);

            // 固定布局为 nonce + tag + ciphertext；外层版本前缀使未来升级可以显式拒绝旧格式。
            var protectedBytes = new byte[nonce.Length + authenticationTag.Length + ciphertext.Length];
            nonce.CopyTo(protectedBytes, 0);
            authenticationTag.CopyTo(protectedBytes, nonce.Length);
            ciphertext.CopyTo(protectedBytes, nonce.Length + authenticationTag.Length);
            return CurrentTokenPrefix + WebEncoders.Base64UrlEncode(protectedBytes);
        }
        finally
        {
            // 明文包含租户和列表位置信息，完成加密后立即清零，缩短其在内存中的存留时间。
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// 验证并解析当前请求使用的游标，明确拒绝篡改、未知版本、跨接口和跨租户令牌。
    /// </summary>
    /// <param name="cursor">客户端原样回传的不透明游标。</param>
    /// <param name="expectedScope">当前列表接口和排序语义对应的范围。</param>
    /// <param name="expectedTenantId">当前已认证租户标识。</param>
    /// <returns>经过认证且可用于数据库键集条件的位置。</returns>
    /// <exception cref="DomainException">游标无效、范围不匹配或租户不匹配时抛出。</exception>
    public CursorPosition Unprotect(string cursor, string expectedScope, Guid expectedTenantId)
    {
        if (string.IsNullOrWhiteSpace(cursor) ||
            cursor.Length > MaximumEncodedCursorLength ||
            !cursor.StartsWith(CurrentTokenPrefix, StringComparison.Ordinal))
        {
            throw InvalidCursor();
        }

        // Base64Url 解码前已经限制总长度，避免恶意查询触发过大的临时数组。
        byte[] protectedBytes;
        try
        {
            protectedBytes = WebEncoders.Base64UrlDecode(cursor[CurrentTokenPrefix.Length..]);
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }

        if (protectedBytes.Length <= NonceSizeInBytes + AuthenticationTagSizeInBytes)
        {
            throw InvalidCursor();
        }

        // 按固定协议布局切分随机数、认证标签和密文，解密失败统一映射为非法游标。
        var nonce = protectedBytes.AsSpan(0, NonceSizeInBytes);
        var authenticationTag = protectedBytes.AsSpan(NonceSizeInBytes, AuthenticationTagSizeInBytes);
        var ciphertext = protectedBytes.AsSpan(NonceSizeInBytes + AuthenticationTagSizeInBytes);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_encryptionKey, AuthenticationTagSizeInBytes);
            aes.Decrypt(nonce, ciphertext, authenticationTag, plaintext, AssociatedData);
            var payload = JsonSerializer.Deserialize<CursorPayload>(plaintext) ?? throw InvalidCursor();
            return ValidatePayload(payload, expectedScope, expectedTenantId);
        }
        catch (CryptographicException)
        {
            throw InvalidCursor();
        }
        catch (JsonException)
        {
            throw InvalidCursor();
        }
        finally
        {
            // 解密后的载荷只在本次请求中使用，解析完成或失败后均立即清零。
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// 校验生成游标前的服务端输入，避免产生无法继续使用的内部令牌。
    /// </summary>
    /// <param name="scope">接口范围。</param>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="position">键集位置。</param>
    private static void ValidateProtectionInput(string scope, Guid tenantId, CursorPosition position)
    {
        if (string.IsNullOrWhiteSpace(scope) ||
            tenantId == Guid.Empty ||
            string.IsNullOrEmpty(position.SortKey) ||
            position.SortKey.Length > MaximumSortKeyLength ||
            position.Id == Guid.Empty)
        {
            throw new InvalidOperationException("A cursor cannot be created from an incomplete pagination position.");
        }
    }

    /// <summary>
    /// 校验已成功解密的载荷，并将安全边界错误映射为稳定的领域错误码。
    /// </summary>
    /// <param name="payload">经过 AES-GCM 认证的载荷。</param>
    /// <param name="expectedScope">当前接口范围。</param>
    /// <param name="expectedTenantId">当前认证租户。</param>
    /// <returns>可用于数据库查询的位置。</returns>
    private static CursorPosition ValidatePayload(
        CursorPayload payload,
        string expectedScope,
        Guid expectedTenantId)
    {
        if (payload.Version != CurrentPayloadVersion ||
            string.IsNullOrEmpty(payload.SortKey) ||
            payload.SortKey.Length > MaximumSortKeyLength ||
            payload.Id == Guid.Empty ||
            payload.TenantId == Guid.Empty)
        {
            throw InvalidCursor();
        }

        if (!string.Equals(payload.Scope, expectedScope, StringComparison.Ordinal))
        {
            throw DomainException.Validation(
                "cursor_scope_mismatch",
                "The cursor does not belong to this resource or sorting mode.");
        }

        if (payload.TenantId != expectedTenantId)
        {
            throw DomainException.Validation(
                "cursor_tenant_mismatch",
                "The cursor does not belong to the authenticated tenant.");
        }

        return new CursorPosition(payload.SortKey, payload.Id);
    }

    /// <summary>创建统一的非法游标领域异常，避免向客户端泄露密码学失败细节。</summary>
    /// <returns>状态码为 400 且错误码稳定的领域异常。</returns>
    private static DomainException InvalidCursor() =>
        DomainException.Validation("invalid_cursor", "The cursor is malformed, unsupported, or has been modified.");

    /// <summary>
    /// 表示加密前的版本化游标载荷；该类型只参与内部 JSON 序列化，不构成公开响应契约。
    /// </summary>
    /// <param name="Version">载荷结构版本。</param>
    /// <param name="Scope">资源和排序方式范围。</param>
    /// <param name="TenantId">游标所属租户。</param>
    /// <param name="SortKey">上一页末项的主排序键。</param>
    /// <param name="Id">上一页末项的唯一决胜键。</param>
    private sealed record CursorPayload(
        int Version,
        string Scope,
        Guid TenantId,
        string SortKey,
        Guid Id);
}
