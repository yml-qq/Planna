# Prompt：AI Orchestrator 与排程执行闭环

任务：{如 AI-03/04，智能计划预览与安全应用}
必读 AGENTS.md、docs/08-AI-CAPABILITIES.md、docs/09-AI-ORCHESTRATION.md、docs/15-SECURITY-PRIVACY.md、spec/ai-plan.schema.json。

实现要求：
- AI Model Provider 接口隔离厂商；必须有 Mock Provider 可稳定测试。
- Model 输出严格 JSON Schema 校验，字段不合法拒绝；上下文仅取用户已授权数据。
- 排程规则引擎独立于模型，实现任务时长、固定事件、依赖、可用时间和时区校验。
- `propose` 只写 AI plan 草稿；`apply` 必须根据 plan_id/snapshot_hash/expected_version/短期确认凭据再次检验，所有实体更新在单事务提交。
- 不给模型 raw SQL、文件系统、任意 HTTP、用户账户注销/删除全部任务等工具。
- 对提供者超时、过期计划、权限被撤回、prompt injection、超预算、模型编造日期做测试。

先输出威胁模型和测试，再实现 .NET 10 AI 模块；最后附真实测试执行结果、模型费用控制策略和人工确认交互契约。
