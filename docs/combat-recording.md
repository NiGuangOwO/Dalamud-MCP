# 按战斗记录技能流

原有 `combat_damage` 是对玩家 HP 降低的采样，不是伤害解析器。新的记录器独立于该环形缓冲区和 500ms 事件节流，通过安装的 FFXIVClientStructs SDK 的 `ActionEffectHandler.Receive` 记录服务端技能效果。

## 使用

1. `combat_recording_configure {"enabled":true,"scope":"local","durationSeconds":600}`。启用受 `AllowMutatingTools` 闸门约束，默认关闭，不保存启用状态。
2. `combat_recording_status {}` 确认 `enabled`、`hooked`、超时及丢包计数。
3. 进入战斗自动创建记录，退出战斗自动以 `combat_exit` 结束；随后进战创建另一场。启用时已经在战斗会标记 `partialStart=true`。
4. `combat_recording_list {}` 列出场次。`combat_recording_read {"count":100}` 读取最新场次，使用返回的 `lastId` 作为下一页 `afterId`；可提供 `encounterId`。读取不会消费记录。
5. `combat_recording_export {}` 将最新场次导出到插件配置目录的 `combat-recordings/combat-<id>.json`，返回实际绝对路径。也可指定 `encounterId`。
6. `combat_recording_configure {"enabled":false}` 停止钩子并保留记录。再次启用会建立新缓冲区，旧数据须先导出。

## 记录内容和边界

- 每场带区域、职业、起止时间、结束原因、部分开场及截断信息。事件带单调递增 ID、UTC Unix 毫秒和相对进战的 `elapsedMs`。
- 记录服务端 `action_effect`：来源/目标/拥有者 ID、技能 ID、本地化技能名、动作类型、服务端及客户端序列、动画锁、每目标最多 8 条原始效果。技能名只按 Action 类型解析。
- 解码直接伤害/治疗的扩展数值、暴击/直击及反射/作用于来源标志。数值是服务端宣告效果，不保证等于实际扣血、有效治疗或 FFLogs DPS。
- `local` 记录自身和自身宠物的动作，以及指向自身的动作；`all` 记录客户端收到的全部附近动作。每 100ms 采样最多 256 个可见战斗角色的读条、状态和死亡状态；`local` 下仅采样自身与自身宠物。范围外对象不保证有完整记录。
- 入战前最多保留 1.5 秒、256 条自身动作，避免丢失开怪技能。此类事件可能 `elapsedMs<0`，标记 `prePull=true`。事件页按采集顺序返回；按时间展示时可用 `(timestamp,id)` 排序。
- 默认保留 5 场、每场 20000 条；可设 1–10 场、100–50000 条。每场另有约 8 MiB 紧凑 JSON 预算，结束标记预留一条。`droppedEvents`、`EvictedEncounters` 明确反映丢弃。原生队列最多 2048 包，丢弃/无效包另有计数。
- 有效期 10–1800 秒（默认 600）。超时、退出登录、关闭变更闸门、停止服务、卸载会停止采集并移除钩子。切换区域结束当前场次。
- 数据在内存，插件重载会丢失。导出文件保留最近 10 个；导出进行中的场次只包含导出时的快照。不会上传。
- 不覆盖周期 DoT/HoT 跳伤归因、技能请求/失败、完整战斗机制、精确读条结束原因或瞬间发生又消失的状态。状态移除也可能表示对象离开采样范围。这不是完整 FFLogs 替代品。
- 导出不含角色姓名，但包含运行时对象 ID；发布测试证据时使用汇总信息。

## 验证

`dotnet run --project tests/CombatRecordingTest -c Release` 验证战斗闸门模型、开怪保留、分页、容量、效果数值、安装 SDK 布局和无效/跨页原生指针。另需 Debug/Release 构建、PluginLoadTest 和 ProtocolSmokeTest。离线结果不证明钩子在游戏中的行为；应先核对 `/health.buildMvid`，再有限度实测。
