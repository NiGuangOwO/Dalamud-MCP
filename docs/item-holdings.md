# 统一道具持有查询

`search_item_holdings` 是只读查询工具。必须提供 `itemId`（基础道具 ID，HQ 与 NQ 共用）或 `name`（游戏客户端语言的道具名称）中的一个。

```json
{"itemId":47197}
```

```json
{"name":"戒指","contains":true,"scopes":["armoury","equipment"],"isHq":true,"maxResults":50}
```

默认按名称精确匹配；`contains=true` 启用子串匹配。`isHq` 省略时同时查询 HQ/NQ。`scopes` 默认包含以下六个范围：

| scope | 范围 |
| --- | --- |
| `inventory` | 四个背包及玩家水晶栏 |
| `equipment` | 当前穿戴装备 |
| `armoury` | 现行兵装库各栏，包含主副手、首饰与灵魂水晶；不包含已废除的腰带栏 |
| `saddlebag` | 两页陆行鸟鞍囊 |
| `premiumSaddlebag` | 两页扩充鞍囊；权限或加载状态不足时标记未知 |
| `retainer` | 已访问雇员的七页仓库、水晶和穿戴装备 |

`totalQuantity`、`nqQuantity`、`hqQuantity` 汇总所有匹配格子，`matchedSlots` 是匹配格子数。`locations` 返回位置、容器、零起点格子、所属雇员及观察时间。`maxResults` 范围 1–200，默认 100；分页使用 `nextOffset`，总量不受分页限制。每次调用重新观察状态，分页期间物品移动可能改变位置或数量。

`coverage` 中每个容器的 `source` 为 `live`、`cached` 或 `unavailable`。只有全部所选范围都实时可读、雇员名单已加载且所有列出的雇员均实时可读时，`complete` 才为 true。名称解析失败也会使名称查询不完整。`complete=false` 时，总量仅代表已观察到的范围，不能把零解释为全角色未持有。

服务器不会主动开窗或请求仓库数据。插件在框架线程被动观察，每秒保存已加载鞍囊和雇员容器。雇员容器的加载标记可能始终为 true，因此不能据此确认当前归属：只有对应雇员的道具管理窗口可见且就绪时，才将其仓库视为实时数据并刷新快照。每帧观察窗口关闭/重新打开及雇员选择变化，拒绝把尚未关闭的旧窗口数据归给新雇员；关闭后只返回原有缓存。正常访问各雇员的物品管理界面即可逐步填充快照。未访问的雇员在 `retainers` 中列为未知；名单尚未加载时 `retainerRosterAvailable=false`。

默认 `includeCached=true`，可查询已关闭仓库的最近快照；缓存可能过期，结果带 `observedAt`，位置还带 `ageMs`。使用 `includeCached=false` 仅读取实时容器。缓存仅保存在内存，登出、换角色或重载插件后清空。

不包含雇员市场在售物品、幻化台、收藏柜、房屋储物及其他角色；这些范围不属于本工具的完整性声明。原有 `get_inventory`、`get_equipment` 的接口不变。

验证：`dotnet run --project tests/ItemHoldingsTest -c Release` 覆盖跨容器/HQ 汇总、分页总量、未加载与空仓区分、雇员缓存隔离、登出/换角色清理、名称解析和参数边界、雇员切换归属保护。插件注册检查另运行 `tests/PluginLoadTest`。离线测试不替代游戏内仓库加载验证。
