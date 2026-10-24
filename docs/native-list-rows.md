# 原生列表和树形列表行

`get_addon_list_row` 和 `activate_addon_list_row` 支持直接归属 `List` 或 `TreeList` 的 `ListItemRenderer`。先通过 `list_addon_elements` 找到实际行文字和完整组件路径。

例如，本次都市传送网的文字节点为 `nodeId=6, componentPath=[9,61004]`。其中 9 是树形列表，61004 是行渲染节点，6 是内部文字节点。它们都不能当作真实行索引。查询渲染节点：

```json
{"addon":"TelepotTown","nodeId":61004,"componentPath":[9],"expectedAddonId":129}
```

查询返回实际 `rowIndex=6`、选中行、列表长度、可见性、更新状态和直接文字节点。每个渲染节点的 ULD 数量最多 256，文字最多 16 个、每条最多 512 字节；未知文字/重复文字节点 ID/超限使 `scanIncomplete=true`。支持的路径、类型、指针与所有权均需验证。

激活要求实际下标和准确的唯一可见文字：

```json
{"addon":"TelepotTown","nodeId":61004,"componentPath":[9],"expectedAddonId":129,"expectedIndex":6,"expectedText":"冒险者行会前（锻铁匠行会/铸甲匠行会/黑涡团）","dryRun":true}
```

执行时去掉 `dryRun` 或设为 false。工具确认列表与行可见/启用、布局已稳定、SDK 返回的渲染器确实是该行、选项未禁用，且列表仅向当前 addon 注册一个 ListItemClick 接收者。若尚未选中，使用 SDK SelectItem 选中它；再次核对行索引、文字、渲染器和布局后，使用 SDK DispatchItemEvent 发送一次 ListItemClick。选择后身份、文字或布局变化时只返回 `selectionDispatched=true,inputDispatched=false`，不激活旧内容；等待稳定并重新查询。

已选中的行仍可被激活，因此激活不是幂等操作。不要因为一次即时观察尚未转区就自动重复发送。`inputDispatched=true` 仅证明发送了输入，实际行为可能是预览、菜单或异步传送，必须延后核对 UI/客户端状态。部分业务可能需要后续已观察到的交互，工具不会猜回调或自动确认。

离线验证覆盖节点编号与真实索引差异、只读发现、预览、重复激活语义、旧下标/文字、禁用/隐藏、回收与文字替换、无转变分支、事件链和函数地址。游戏内确认目标渲染节点 61004 对应真实行 6；首次激活观察到地图预览，再次激活后延后观察到区域 129→128、地图 12→11，附近以太之晶与目标相符，金币未变。其他列表及业务行为未在本次运行中验证。
