# Prompt：离线同步协议实施

任务：实现 `SYNC-01/02/03` 中的最小完整切片。只修改 server 同步模块与相关数据 migration/测试；客户端先提供协议 fixture。

必读 docs/07-OFFLINE-SYNC.md 与 docs/05-DATA-DOMAIN.md。高风险点：不能用 `updated_at` 当 cursor；不能用裸 BIGSERIAL 作为提交序的唯一证明；必须每账户行锁串行化 revision 递增，事务提交前保持锁；服务端 response/mutation 去重也必须与业务数据原子一致。

测试必须包含：同 mutation 重放、成功后 ack 丢失重试、两个并发写事务交错提交、客户旧 base_version、同 owner 不同设备冲突、跨 owner 数据隔离、拉取中断本地事务回滚、tombstone、cursor 过期安全重建。

输出：协议状态机、EF Core 数据迁移、C# application use cases、错误处理、契约示例、可复现并发测试命令与实际运行结果。
