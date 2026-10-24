# DalamudMCP 1.0.0.25

本次更新来自一次“用 MCP 实际打游戏”的复盘：在游戏内跑场景、记录没有可用工具或工具结论不可信的地方，然后只补齐能够正确验证的那部分。

新增六个工具。`get_position` 只读本地玩家读位置、朝向、罗盘方位、区域与地图，并可选报告到某个物体的距离，比 `get_local_player` 便宜得多；`move_to_position` 支持直接走到原始世界坐标（先吸附到导航网格，再交给 vnavmesh），`stop_movement` 用于取消自动移动，它的 vnavmesh 停止入口不是仓库里已知的 IPC 名，因此按固定顺序探测候选名，只调用 `HasFunction`/`HasAction` 证明存在的那个，探测不到时明确说明“可能仍在移动”而不是假装停止。`get_gearsets` 与 `equip_gearset` 解决“没法可靠切职业”：列表给出 36 个套装、当前索引、职业、品级与失效标记，切换按 id 或名称（精确优先，其次唯一子串，歧义拒绝）执行并轮询确认切换后的索引与职业。`get_cooldowns` 回答“哪些技能在冷却”，排除 GCD 组，避免武器技能淹没结果。

同时修正了两个比缺工具更糟的行为：`execute_action` 不给 `targetObjectId` 时会用自身目标 id（`0xE0000000`）并在没有命中时返回 `success: true`；现在按“显式 id → 当前硬目标 → 自身”解析，并报告 `targetName`/`targetSource`，被拒绝时带上 `GetActionStatus` 状态码与原因说明。`use_general_action`、`toggle_sprint`、`jump`、`toggle_autorun`、`dismount`、`accept_raise` 同样在拒绝时给出状态码与原因，不再是裸的 `{"success": false}`。既有字段（`actionId`、`targetObjectId`、`success`）以及 `move_to_entity`/`move_to_nearby_targetable_object` 的参数、返回键与错误文本保持不变。

游戏内验证：`get_position` 返回 253.5/106.0/−214.23、rotation 0.3788、罗盘 158.3°，与按世界坐标算出的木人（1073748164）方位 158.2° 一致；`move_to_position` 到 (253.5, 106, −212) 后 z 从 −214.23 变为 −212.2（真的走了 2 亚丈），罗盘 179.9° 与“rotation 0 朝南”一致；`stop_movement` 探测到 `vnavmesh.Path.Stop` 并调用。36 个套装读取正常，按名称切到“骑士”得到 `equipResultCode 0`、索引 15→0、职业 31→19（PLD），再按 id 切回 15 恢复 MCH。`execute_action` 干跑“野火”报告 `targetSource: currentTarget`、状态 0，实放后 `inCombat` 为 true；干跑“分裂弹”返回状态 572 及连击/状态不满足的说明。`get_cooldowns` 在未行动时为空并附说明，放完“野火”后报告 recastGroup 19、剩余 117.5/总计 120 秒。

验证：Debug/Release 均 0 警告 0 错误；PluginLoadTest 160/160（工具数断言同步到 137），ProtocolSmokeTest 50/50。全程未使用道具、未购买、未改动装备；唯一持久改动是切换套装，结束时已切回原套装 15。未实现的能力（使用道具、生产采集、市场与商店、任务接取交付、雇员、队伍与社交、背包整理、单件装备更换）连同原因记录在 `docs/tool-gap-audit-2026-10-24.md`。

运行 Debug MVID：`7b02df31-6687-436a-8ac4-ac6af1d5057e`；Release MVID：`8777d052-66f2-4a4c-8074-016f77ddbd84`。`latest.zip` SHA-256：`11FA449D5CC72D9DADDE066026FC28EE22086D5A966ECD81D2B90313C4D716B3`。
