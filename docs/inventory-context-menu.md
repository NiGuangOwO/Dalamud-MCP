# 背包物品菜单的发现和打开

`open_addon_context_menu` 只支持 schema 允许列表中的普通随身背包格子窗口，以及实时发现的、可见且启用的 DragDrop 控件。它使用当前 SDK 的事件数据结构，发送一次已注册的右键 `DragDropClick`。不发送 DragDropBegin/Insert、左键或菜单选项。快捷栏、任务物品、雇员和其他窗口不在输入范围内。

先用 `list_addon_elements` 找到确切 `nodeId`/`componentPath`，再预检 `dryRun=true`。可用 `expectedAddonId` 校验当前窗口 ID，但 ID 本身不是生命周期令牌。已有 ContextMenu 时拒绝再次打开；不要用通用 `event="registered"` 模拟物品右键，它会发送不止一种事件。

校验包括控件类型和所有权、有效可见性、内层 Icon 节点与 DragDrop 的 ULD 及父节点关系、内层图标组件所有权、唯一的所属 addon DragDropClick 监听器，以及可执行的 ReceiveEvent。遍历事件链最多 64 项；不可读、重复或循环事件被拒绝。事件参数 0 也是有效值，不替换成默认参数。事件中的外层节点和数据中的内层 Icon 节点分别按当前 SDK 布局填入。

`requested`/`dispatched` 仅表示请求已发出。随后检查真实 ContextMenu，并调用只读 `get_inventory_context`。后者只在库存上下文代理与当前可见菜单 ID 匹配时返回实际背包类型、槽位、原生物品 ID、数量、原生标记和所属窗口。不可读、空、符号或 dummy 物品以及与上下文槽位不一致的数据均不可用。原生 ID/标记不等于 Dalamud 编码后的 HQ 物品 ID；用 `get_inventory` 匹配返回的 container/slot，核对名称、baseItemId 和数量。

显示格子、DragDrop 引用索引与物理背包槽位可能受界面排序影响，不能从节点编号推测物品身份。菜单内容也可包含插件扩展项；发现标签和实际列表次序后再选择。`ContextMenu` 不是 `SelectString`，不能把它的 AtkValue 字段索引直接当作菜单序号。

2026-10-24 实测：在 InventoryLarge 布局的 InventoryGrid0 上，预检不打开菜单；右键一次后出现 ContextMenu。实时上下文读到 Inventory2/27、物品 49235、HQ、66 个，与 `get_inventory` 中的高品质“4级巧力之宝药”一致。重复打开已有菜单被拒绝。通过已观察到的“拆分”列表项进入 InputNumeric，显示范围 1–65；点击“取消”后弹窗关闭，物品仍为 66 个，没有完成拆分。关闭背包同时关闭菜单，再查上下文返回不可用。

离线测试覆盖独立可执行 ReceiveEvent 回调、真实 SDK 事件负载、内层/外层节点区分、预检、参数 0、所有权、允许列表、可见性、事件链和执行地址边界，以及上下文目标匹配。其他布局和物品菜单未逐一验证；右键菜单出现不证明后续业务操作成功。

Debug/Release 构建均为零警告、零错误；NativeUiReadTest 在两个构建上通过 121/121，Release PluginLoadTest 通过 146/146，ProtocolSmokeTest 通过 50/50。运行中的 Debug MVID 与测试程序集一致。目录为 122 个工具，其中 74 个只读、48 个可变更。
