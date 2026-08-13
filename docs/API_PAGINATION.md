# API 游标分页

## 适用范围

当前 `GET /api/contacts` 和 `GET /api/groups` 支持稳定的键集游标分页。两个接口均按 `displayName` 升序排列，并以唯一 `id` 升序作为同名记录的决胜键。游标同时保存这两个排序键，因此同一名称跨页时不会仅因重名发生重复或漏项。

分页保证的是请求之间未改变排序键的数据集上的稳定遍历。若遍历期间新增、删除记录，或修改已经存在记录的 `displayName`，新快照可能相对当前游标移动；需要严格快照语义的调用方仍应使用数据库快照或独立导出任务。

## 请求契约

新版调用方在第一页传入 `pageSize`：

```http
GET /api/contacts?pageSize=100
X-Api-Key: <admin-key>
```

`pageSize` 可取 `1` 到 `500`，默认值为 `100`。后续页必须原样回传上一页的 `nextCursor`，并继续对查询参数执行 URL 编码：

```http
GET /api/contacts?pageSize=100&cursor=<url-encoded-nextCursor>
X-Api-Key: <admin-key>
```

响应结构统一为：

```json
{
  "items": [],
  "nextCursor": null,
  "hasMore": false
}
```

- `items` 是当前页数据；没有后续匹配记录时允许为空数组。
- `nextCursor` 仅在还有后续记录时存在，调用方不得解析、拼接或修改。
- `hasMore` 与 `nextCursor` 是否存在始终一致。
- 末页和有效游标之后的空页都返回 `nextCursor: null`、`hasMore: false`。

## 兼容模式

未出现 `pageSize` 或 `cursor` 时，接口继续采用旧版数组响应，并保留 `take` 参数：

```http
GET /api/groups?take=100
```

旧版 `take` 同样限制为 `1` 到 `500`。`take` 不得与 `pageSize` 或 `cursor` 混用；混用会返回 `400` 和 `conflicting_page_size`。新集成应使用游标模式，旧数组模式仅用于平滑迁移现有管理端和调用方。

## 游标安全

游标使用 AES-256-GCM 保护，密文包含协议版本、资源及排序范围、租户 ID、排序键和唯一 ID。每次生成均使用新的随机数，因此游标不可预测；认证标签会检测任何篡改。服务端还会校验资源范围和当前认证租户，联系人游标不能用于群接口，其他租户的游标也不能重放。

生产环境必须通过安全配置注入独立秘密：

```powershell
$env:Pagination__ProtectionKey = '<至少 32 个字符的高熵随机秘密>'
```

该密钥不得与管理员 API Key、Agent API Key、激活码 pepper、审计完整性密钥或备份密钥复用，不得记录到日志或进入版本控制。轮换该密钥会使轮换前签发且尚未使用的游标失效，客户端应从第一页重新遍历。

## 错误码

| 错误码 | HTTP | 条件 |
| --- | --- | --- |
| `invalid_page_size` | `400` | `pageSize` 或旧版 `take` 超出 `1..500` |
| `conflicting_page_size` | `400` | `take` 与 `pageSize`/`cursor` 混用 |
| `invalid_cursor` | `400` | 游标为空、过长、格式错误、版本未知或被篡改 |
| `cursor_scope_mismatch` | `400` | 游标用于不同资源或排序语义 |
| `cursor_tenant_mismatch` | `400` | 游标所属租户与当前认证租户不同 |

所有错误使用现有 `ProblemDetails` 响应，稳定错误码位于 `errorCode` 扩展字段中。密码学解析失败只返回通用 `invalid_cursor`，不会暴露密钥、载荷或认证失败细节。

## 数据库与性能依据

实现使用 EF Core 10 键集条件并额外读取一条记录判断 `hasMore`，不会执行总数统计。联系人和群均建立与全局租户过滤及排序一致的复合索引 `(TenantId, DisplayName, Id)`；当前 SQLite 开发数据库通过迁移创建该索引。切换 PostgreSQL 或使用真实生产数据后，仍需重新检查执行计划并用代表性数据量进行基准测试，不能仅凭算法形式宣称吞吐指标。
