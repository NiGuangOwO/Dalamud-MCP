# DalamudMCP 1.0.0.6

- 新配置的战斗记录范围默认为 `all`，包括客户端收到的附近队友、敌人和 Boss 技能效果，以及现有有界的读条/状态采样。
- 无效范围回退到 `all`；有效的已有 `local`/`all` 设置仍保留，MCP 调用省略 `scope` 时保留当前设置。可以继续显式选择 `local`。
- 状态在采集启动前也反映配置范围。同步工具参数说明和中英文文档。

验证：Debug/Release 构建零警告零错误；PluginLoadTest 124/124，覆盖默认值、保留有效 local 及无效值回退；ProtocolSmokeTest 50/50。本机已将保存的范围切换为 `all`，重载后确认后台持续采集、scope=all，正式运行 MVID 与构建匹配。

发布构建 MVID：`fe92f7bf-cde0-4042-ba66-e43699a64f71`。包 SHA-256：`0C922A8CA9064A81A8D6EE8F435816FA7E48BA4F82C7A726C3C31FA4FC8096D5`。

本次未执行新的多人战斗测试；此前八人副本验证使用 local，不能当作 all 模式的性能或完整性验证。all 仍受客户端可见范围、采样及事件容量限制，不是完整 FFLogs。自动归档上限仍为 7 天、1000 场、100 MiB。见[记录说明](combat-recording.md)。
