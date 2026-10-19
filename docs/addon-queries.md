# Addon 查询与精简结果

`list_addon_elements` 接受 `list_loaded_addons` 返回的任意已加载窗口名称；名称不受 `open_addon` 允许列表限制。默认仍返回完整字段，兼容原有调用。普通查找建议使用精简结果和服务端筛选，避免传输整棵节点树再由模型编写过滤代码。

```json
{"addon":"DRAutoRetainerWork","label":"改价（全部雇员）","componentType":"Button","visibleOnly":true,"enabledOnly":true,"detail":"compact"}
```

- `label`：去除首尾空白后、不区分大小写的字面精确匹配，最多 256 字符；`containsMatch=true` 改为子串匹配，必须提供非空标签。不会执行正则或自动选择某个候选。
- `componentType`：按 SDK 组件类型筛选，例如 `Button`、`CheckBox`；合法名称在工具 schema 的枚举中。
- `visibleOnly`、`enabledOnly`、`clickableOnly`：可组合，默认均为 false；状态沿用已有节点读取逻辑，不代表操作已获授权或事件一定有效。
- `detail=compact`：仅返回 `nodeId`、`componentPath`、组件类型、标签、可见/启用/可点击状态；`full` 保留原有类型、深度、坐标和尺寸。
- `maxNodes`：限制筛选后的匹配数，范围 1–500，默认 100。无关节点不消耗结果预算；发现额外一个匹配后标记截断。`allNodes` 默认 false，仍只考虑有标签或被识别为可点击的节点。

每个节点新增 `componentPath`，按最外层到最内层记录所属组件 ID。顶层节点路径为 `[]`。将该路径和 `nodeId` 复制到 `click_addon_element`；快照和采样工具则将节点 ID 放入 `nodeIds` 数组。不要同时传入 `componentId`。每个路径层级按独立 ULD 数组精确查找，校验原生组件所有权，遇到重复 ID、不可读数据或循环路径会拒绝解析，不回退到另一个范围内的同号节点。

```json
{"addon":"窗口名","nodeIds":[2],"componentPath":[12,5]}
```

此例读取组件 12 内的组件 5 内的节点 2，不表示它适用于任意窗口。路径最多 40 层；路径不是快照句柄或生命周期令牌，窗口替换、重新打开或游戏重启后须重新查询。点击仍需选择经过验证的事件契约；本次更新没有自动猜测或重试事件。

`sample_addon_nodes` 同样支持此路径；`click_addon_when_node_matches` 的 `componentPath` 同时限定观察、目标与点击节点，触发时仍使用同一路径定位。

`returned` 表示本次返回的节点数，`scannedNodes` 表示访问过的唯一原生节点地址数。遍历最多访问 4096 个节点，每个 ULD 数组最多 512 项，组件深度由 `maxDepth` 限制。`scanIncomplete=true` 表示深度、读取或遍历边界使扫描不完整；此时或结果数超限时，`truncated=true`。截断结果不能证明目标不存在或唯一。

## 验证

```powershell
dotnet run --project tests/AddonQueryTest -c Release
dotnet run --project tests/NativeUiReadTest -c Release
dotnet run --project tests/ProtocolSmokeTest -c Release
dotnet run --project tests/PluginLoadTest -c Release
./tests/AddonQueryTest/LiveComparison.ps1
```

`LiveComparison.ps1` 仅调用只读工具，遍历完整工具目录，在同一已打开窗口中比较 full、compact 和标签过滤结果，并将返回路径用于快照读取。默认测试 `_ActionBar` 的标签 `7`，可通过 `-Addon`、`-Label` 调整；窗口扫描不完整、目标缺失或结果不一致时会失败。运行前须将 `/health.buildMvid` 与已测试 DLL 核对。

2026-10-19 初次现场验证：快捷栏 320 节点，full 为 111014 UTF-8 字节，compact 为 72795 字节（减少 34.43%）；标签过滤后为 1 节点、414 字节（减少 99.63%），路径成功用于快照读取。单次 HTTP 批次分别约 33.1、12.3、8.6 毫秒，受现场帧和调度影响，不作性能保证。该测试没有测量模型内部思考时间，也没有执行游戏点击。
