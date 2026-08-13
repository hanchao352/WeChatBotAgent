# SQLite 写锁约束

备注任务租约状态转换和 Agent 群提及上报必须把“当前凭据仍有效”的数据库复核与随后的业务写入放在同一个串行化区间内。当前后端依赖 `Microsoft.Data.Sqlite 10.0.10`，生产路径通过 EF Core 开启 `IsolationLevel.Serializable` 事务，然后执行一条不修改任何行的条件更新来确认目标表写锁：

```sql
UPDATE RemarkTasks SET Version = Version WHERE 0 = 1;
```

群提及上报对 `GroupMentions` 使用对应的零行更新。语句影响零行，但当前 SQLite 提供程序会把该事务升级为写事务并持有数据库写锁。身份复核、租约或群事件写入以及凭据轮换/吊销都在各自事务中完成；竞争写事务只能在首事务提交或回滚后继续，因此不能插入复核与业务写入之间。若事务开始、探针执行或后续写入阶段遇到持续竞争，SQLite 会返回 `SQLITE_BUSY`/`SQLITE_LOCKED`。领取接口经过有限退避后返回 `409 remark_task_claim_busy`；其他写接口由统一异常处理器返回 `409 database_write_busy`，不会把可恢复的单写者竞争暴露为 500。

连接串中的 `Default Timeout` 只规定竞争连接等待锁的最长时间，不改变事务的锁范围、隔离级别或“提交后才释放”语义。生产配置保持有限等待，业务层再将可恢复的 `SQLITE_BUSY`/`SQLITE_LOCKED` 竞争转换为明确的冲突响应；测试锁探针使用更短的等待上限，但确定性来自同步闸门而不是固定延时。

`SqliteWriteLockIntegrationTests.Zero_row_update_acquires_sqlite_write_lock` 使用两个关闭连接池的独立连接验证底层语义：首连接在 `Serializable` 事务中执行零行更新后，第二连接立即开始与生产路径相同的非延迟写事务必须收到 `SQLITE_BUSY` 或 `SQLITE_LOCKED`。HTTP 锁耗尽回归进一步验证领取返回 `409 remark_task_claim_busy`，续租、释放、Agent 完成、管理员完成和 Agent 群提及上报返回 `409 database_write_busy`，均不会泄漏 SQLite 驱动异常。凭据竞态测试还在 TestServer 中注入同步观察点，使用第二 SQLite 连接确认首事务仍持有写锁；释放闸门后验证业务请求和轮换/吊销都提交成功，并用旧凭据发送后续心跳确认返回 `401 Unauthorized`。管理员完成竞态测试使用同一机制把请求停在租约复核后，确认并发 Agent 认领只能在管理员终态提交后执行且返回空队列。

升级 `Microsoft.Data.Sqlite`、切换数据库提供程序或修改事务模式时必须先运行上述锁测试和凭据竞态测试。若锁语义发生变化，应改用目标数据库明确支持的写锁或条件更新方案，不能删除原子性保障。
