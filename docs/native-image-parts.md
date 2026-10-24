# 原生图片分片观察

`get_addon_node_snapshot` 和基于它的 `sample_addon_nodes` 为 Image 节点增加可选的 `image` 对象，不增加输入、回调或纹理加载操作。

```json
{"addon":"Puzzle","componentPath":[19],"nodeIds":[6]}
```

先通过 `list_addon_elements`/节点快照确认真实 Image 节点和完整组件路径。`image` 返回：

- `available`：选中图集分片的结构与矩形是否可读。
- `partId`、`partsListId`、`partCount`：当前客户端该 ULD 图集中的局部编号与容量，不是文字或全局图标编号。
- `u/v/width/height`：选中分片的原生图集矩形。
- `assetAvailable`、`assetId`、`textureType`：可选的局部资源身份和纹理判别类型。
- 仅 Resource 类型继续读取 `resourceAvailable/nativeIconId/iconId/texturePathHash`。原生 icon 值 0 或 uint.MaxValue 保留在 `nativeIconId`，`iconId=null`；工具不把哨兵值当作有效图标。路径哈希不是文件路径，也不保证文字语义。

每层使用完整的受保护 SDK 结构副本，图集容量限制为 4096，只读取当前选中的分片，拒绝越界和地址加法溢出。Crest/KernelTexture 的联合体不会按 Resource 解引用。部分资源不可读时保留已确认的分片几何；`available` 不代表纹理已渲染完成。

可以将图片分片与同一次运行中已观察的图案校准，但不能凭局部 ID 猜测另一图集的含义，也不能跨版本硬编码。本次数字推盘在开始前展示了顺序数字，MCP 读取的分片 0..14 对应 1..15；15 是完成图案。进行中空格控件隐藏，结束后完成图案会重新显示。标量 AtkValue 仍可能保持旧值，须核对完整棋盘、实际按钮和画面，不猜测未定义的状态枚举。

验证：离线覆盖结构/分片/资源指针、容量、边界、溢出、纹理类型联合体和图标哨兵值。游戏内逐步核对棋盘，按求解结果派发 44 个相邻方块点击；前 43 步逐步匹配，最后完成图案触发执行器的保守停止，随后重新读取完整 0..15 分片顺序并确认“再次挑战”按钮及 44 回合通关画面。没有修改棋盘内存或游戏结果。
