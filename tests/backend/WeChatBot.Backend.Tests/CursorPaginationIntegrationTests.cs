using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Tests;

/// <summary>
/// 验证联系人和群列表的游标分页契约、安全边界及旧数组响应兼容性。
/// </summary>
public sealed class CursorPaginationIntegrationTests : IClassFixture<TestApplicationFactory>
{
    /// <summary>按 Web 默认规则反序列化分页响应和问题详情。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>当前测试类共享的隔离 Web 应用工厂。</summary>
    private readonly TestApplicationFactory _factory;

    /// <summary>
    /// 初始化游标分页集成测试。
    /// </summary>
    /// <param name="factory">由 xUnit 创建并在测试类结束后释放的应用工厂。</param>
    public CursorPaginationIntegrationTests(TestApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// 验证联系人键集分页在重名数据下仍按显示名称和唯一 ID 稳定遍历，且旧调用继续收到数组。
    /// </summary>
    [Fact]
    public async Task Contacts_cursor_pages_are_stable_and_legacy_requests_keep_array_contract()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var uniquePrefix = $"cursor-contact-{Guid.NewGuid():N}";
        var expected = await SeedContactsAsync(uniquePrefix);

        // 旧管理端不发送 pageSize/cursor，因此必须继续得到 JSON 数组而不是分页包装对象。
        using var legacyResponse = await client.GetAsync("/api/contacts?take=2");
        Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
        using (var legacyJson = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, legacyJson.RootElement.ValueKind);
            Assert.Equal(2, legacyJson.RootElement.GetArrayLength());
        }

        // 每页只取两条，迫使同名联系人跨页并验证唯一 ID 决胜键不会造成重复或漏项。
        var observed = new List<ContactListItem>();
        string? cursor = null;
        do
        {
            var uri = cursor is null
                ? "/api/contacts?pageSize=2"
                : $"/api/contacts?pageSize=2&cursor={Uri.EscapeDataString(cursor)}";
            using var response = await client.GetAsync(uri);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            var page = JsonSerializer.Deserialize<CursorPage<ContactListItem>>(body, JsonOptions);
            Assert.NotNull(page);
            Assert.InRange(page.Items.Count, 1, 2);
            Assert.Equal(page.HasMore, page.NextCursor is not null);
            observed.AddRange(page.Items.Where(x => x.DisplayName.StartsWith(uniquePrefix, StringComparison.Ordinal)));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(expected, observed.Select(x => x.Id).ToArray());
        Assert.Equal(expected.Length, observed.Select(x => x.Id).Distinct().Count());
    }

    /// <summary>
    /// 验证群列表能返回空页和末页语义，并且超出上下限的页容量被明确拒绝。
    /// </summary>
    [Fact]
    public async Task Groups_cursor_pages_handle_empty_last_page_and_page_size_boundaries()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var uniquePrefix = $"cursor-group-{Guid.NewGuid():N}";
        var expected = await SeedGroupsAsync(uniquePrefix);

        // 先用一页覆盖全部新增群，末页必须同时返回 hasMore=false 与 nextCursor=null。
        using var response = await client.GetAsync("/api/groups?pageSize=500");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var page = JsonSerializer.Deserialize<CursorPage<GroupListItem>>(body, JsonOptions);
        Assert.NotNull(page);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal(expected, page.Items
            .Where(x => x.DisplayName.StartsWith(uniquePrefix, StringComparison.Ordinal))
            .Select(x => x.Id)
            .ToArray());

        // 显式空 pageSize 仍表示调用方选择分页协议，只是页容量回退到统一默认值，不能返回旧数组。
        using var defaultSizeResponse = await client.GetAsync("/api/groups?pageSize=");
        Assert.Equal(HttpStatusCode.OK, defaultSizeResponse.StatusCode);
        using (var defaultSizeJson = JsonDocument.Parse(await defaultSizeResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Object, defaultSizeJson.RootElement.ValueKind);
            Assert.True(defaultSizeJson.RootElement.TryGetProperty("items", out _));
        }

        // 由末项人工生成一个有效游标，验证起点之后没有记录时仍返回结构完整的空分页对象。
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<CursorProtector>();
        var last = page.Items[^1];
        var endCursor = protector.Protect(
            "groups:display-name-id:v1",
            TestApplicationFactory.TenantId,
            new CursorPosition(last.DisplayName, last.Id));
        using var emptyResponse = await client.GetAsync(
            $"/api/groups?pageSize=10&cursor={Uri.EscapeDataString(endCursor)}");
        var emptyPage = await emptyResponse.Content.ReadFromJsonAsync<CursorPage<GroupListItem>>(JsonOptions);
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        Assert.NotNull(emptyPage);
        Assert.Empty(emptyPage.Items);
        Assert.False(emptyPage.HasMore);
        Assert.Null(emptyPage.NextCursor);

        await AssertProblemCodeAsync(client, "/api/groups?pageSize=0", "invalid_page_size");
        await AssertProblemCodeAsync(client, "/api/groups?pageSize=501", "invalid_page_size");
        await AssertProblemCodeAsync(client, "/api/groups?take=1&pageSize=1", "conflicting_page_size");
    }

    /// <summary>
    /// 验证游标不能被篡改、跨资源重用或跨租户重放，且错误响应不泄露载荷内容。
    /// </summary>
    [Fact]
    public async Task Cursor_is_authenticated_and_bound_to_resource_and_tenant()
    {
        using var client = _factory.CreateAuthenticatedClient();
        await SeedContactsAsync($"cursor-security-{Guid.NewGuid():N}");

        using var firstResponse = await client.GetAsync("/api/contacts?pageSize=1");
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<CursorPage<ContactListItem>>(JsonOptions);
        Assert.NotNull(firstPage?.NextCursor);

        // 修改最后一个字符会破坏 AES-GCM 认证标签，服务端只能返回通用非法游标错误。
        var cursor = firstPage.NextCursor!;
        var replacement = cursor[^1] == 'A' ? 'B' : 'A';
        var tamperedCursor = cursor[..^1] + replacement;
        await AssertProblemCodeAsync(
            client,
            $"/api/contacts?pageSize=1&cursor={Uri.EscapeDataString(tamperedCursor)}",
            "invalid_cursor");

        // 联系人游标即使密码学有效，也不能改变用途后用于群列表。
        await AssertProblemCodeAsync(
            client,
            $"/api/groups?pageSize=1&cursor={Uri.EscapeDataString(cursor)}",
            "cursor_scope_mismatch");

        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<CursorProtector>();
        var foreignTenantCursor = protector.Protect(
            "contacts:display-name-id:v1",
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            new CursorPosition("foreign-tenant-position", Guid.NewGuid()));
        await AssertProblemCodeAsync(
            client,
            $"/api/contacts?pageSize=1&cursor={Uri.EscapeDataString(foreignTenantCursor)}",
            "cursor_tenant_mismatch");

        await AssertProblemCodeAsync(client, "/api/contacts?cursor=", "invalid_cursor");
    }

    /// <summary>
    /// 在数据库中直接写入一组名称可排序且包含重名的联系人，避免通过 API 产生额外审计噪声。
    /// </summary>
    /// <param name="prefix">确保本测试数据与同一工厂内其他用例隔离的名称前缀。</param>
    /// <returns>按数据库契约排序后的预期联系人 ID。</returns>
    private async Task<Guid[]> SeedContactsAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var contacts = new[]
        {
            NewContact(prefix, "02", now),
            NewContact(prefix, "01", now),
            NewContact(prefix, "01", now),
            NewContact(prefix, "03", now)
        };
        db.Contacts.AddRange(contacts);
        await db.SaveChangesAsync();
        return contacts.OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
    }

    /// <summary>
    /// 构造一条满足领域和数据库约束的测试联系人记录。
    /// </summary>
    /// <param name="prefix">名称和外部 ID 的隔离前缀。</param>
    /// <param name="nameSuffix">用于制造确定顺序和重名场景的名称后缀。</param>
    /// <param name="now">本批测试数据共享的 UTC 时间。</param>
    /// <returns>尚未持久化的联系人实体。</returns>
    private static Contact NewContact(string prefix, string nameSuffix, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TestApplicationFactory.TenantId,
        ExternalId = $"{prefix}-{Guid.NewGuid():N}",
        DisplayName = $"{prefix}-{nameSuffix}",
        CreatedAt = now,
        UpdatedAt = now
    };

    /// <summary>
    /// 在数据库中直接写入一组名称可排序且包含重名的群，返回稳定预期顺序。
    /// </summary>
    /// <param name="prefix">确保测试数据隔离的名称前缀。</param>
    /// <returns>按数据库契约排序后的预期群 ID。</returns>
    private async Task<Guid[]> SeedGroupsAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var groups = new[]
        {
            NewGroup(prefix, "02", now),
            NewGroup(prefix, "01", now),
            NewGroup(prefix, "01", now),
            NewGroup(prefix, "03", now)
        };
        db.Groups.AddRange(groups);
        await db.SaveChangesAsync();
        return groups.OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
    }

    /// <summary>
    /// 构造一条满足领域和数据库约束的测试群记录。
    /// </summary>
    /// <param name="prefix">名称和外部 ID 的隔离前缀。</param>
    /// <param name="nameSuffix">用于制造确定顺序和重名场景的名称后缀。</param>
    /// <param name="now">本批测试数据共享的 UTC 时间。</param>
    /// <returns>尚未持久化的群实体。</returns>
    private static GroupChat NewGroup(string prefix, string nameSuffix, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TestApplicationFactory.TenantId,
        ExternalId = $"{prefix}-{Guid.NewGuid():N}",
        DisplayName = $"{prefix}-{nameSuffix}",
        CreatedAt = now,
        UpdatedAt = now
    };

    /// <summary>
    /// 发送一个预期失败的列表请求并验证标准问题详情错误码。
    /// </summary>
    /// <param name="client">已通过管理员鉴权的 HTTP 客户端。</param>
    /// <param name="uri">待验证的相对请求地址。</param>
    /// <param name="expectedErrorCode">预期稳定错误码。</param>
    private static async Task AssertProblemCodeAsync(
        HttpClient client,
        string uri,
        string expectedErrorCode)
    {
        using var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(expectedErrorCode, document.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>表示测试只关心的联系人列表字段。</summary>
    /// <param name="Id">联系人唯一 ID。</param>
    /// <param name="DisplayName">联系人主排序名称。</param>
    private sealed record ContactListItem(Guid Id, string DisplayName);

    /// <summary>表示测试只关心的群列表字段。</summary>
    /// <param name="Id">群唯一 ID。</param>
    /// <param name="DisplayName">群主排序名称。</param>
    private sealed record GroupListItem(Guid Id, string DisplayName);
}
