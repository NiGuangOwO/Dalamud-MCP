# Menu selection / 菜单选择

`select_addon_menu_item` reads `SelectString` entries from the installed SDK's native popup structure.
It validates the owner, list component type, entry count, visibility, enabled state and label pointers before input.
The title and other `AtkValue` fields are not selectable entries.

`SelectString` 的选项来自原生 PopupMenu，输入前校验所属窗口、组件类型、选项数量、可见性、启用状态及文本指针。
标题和其他 `AtkValue` 字段不作为菜单选项。

```json
{"addon":"SelectString","label":"都市传送网","containsMatch":true,"dryRun":true}
```

The result identifies `indexKind=menuOrdinal` and a zero-based `index`; the first entry is 0 even when its text is at `AtkValue[7]`.
Remove `dryRun` to dispatch the selected list item's `ListItemClick` through the SDK.
Selecting/highlighting a list item alone is insufficient to activate it.

结果中的 `indexKind=menuOrdinal` 表示从零开始的选项编号。首项为 0，即使它的文本位于 `AtkValue[7]`。
移除 `dryRun` 后通过 SDK 派发选项的 `ListItemClick`；仅设置列表选中状态不会激活选项。

Ambiguous labels are refused, including substring matches. Use the complete label or an explicit menu ordinal.
`selected` and `inputDispatched` report dispatch only. Read the next addon or game state to verify the transition.

包含匹配出现多个候选项时也会拒绝操作，应使用完整标签或明确的选项编号。
`selected` 和 `inputDispatched` 仅表示已派发输入；需读取后续界面或游戏状态确认结果。

Other addons retain legacy `AtkValue`-index callback behavior for compatibility. This does not establish a valid selection contract for arbitrary windows.
Raw callbacks require a known addon-specific contract; do not derive callback arguments from text positions alone.

其他 addon 保留旧版按 `AtkValue` 索引提交回调的兼容行为，不表示任意窗口均适用。
原始回调必须有明确的窗口参数约定，不能仅根据文本位置推测。

The `click_addon_element` list-index shortcut now searches the bounded ULD node array instead of child/sibling links.
Negative/out-of-range indices, unreadable arrays and ambiguous lists are refused.

`click_addon_element` 的列表索引路径改为有界 ULD 数组查找，拒绝负索引、越界索引、不可读数组和不唯一的列表。

Offline `NativeUiReadTest` covers popup ordinals, invalid owners/labels, entry bounds, disabled lists and ULD discovery.
Live verification on 2026-10-17: choosing the observed Aethernet entry opened `TelepotTown` from `SelectString`.
This is one verified menu transition, not certification of all addon layouts.

离线测试覆盖选项编号、无效所属窗口与文本、数量上限、禁用列表及 ULD 查找。
2026-10-17 现场验证：选择已观测的“都市传送网”后，由 `SelectString` 转入 `TelepotTown`。
该结果仅验证此菜单转换，不代表全部界面布局均已验证。
