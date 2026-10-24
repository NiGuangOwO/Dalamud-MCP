# 背包窗口的打开、关闭和状态

从 1.0.0.15 起，`open_addon("inventory")` 与 `close_addon("inventory")` 使用当前 SDK 的 `UIModule.OpenInventory(0)` 和 `UIModule.CloseInventory()`。0 是 SDK 文档定义的普通物品背包类型。其他窗口继续使用各自 agent 的 Show/Hide 路径。

背包 agent 常驻并可在所有背包窗口关闭时仍报告活动；因此 `get_addon_state` 对 inventory 使用 `UIModule.IsInventoryOpen()`。状态读取失败时列入 `unknown`，不伪装成关闭。其他 agent 的活动状态仍不保证窗口可见。

背包打开/关闭结果包含 `requested`、`dispatched`、`beforeOpen`、实际 `open`、`observed` 与 `observation="immediate"`。对应的 `opened` 或 `closed` 表示立即读取到的目标状态，不能只因函数调用返回而设为成功。重复请求相同状态不再次调用打开/关闭函数。若立即观察未达目标，仍需通过新一次状态或已加载窗口查询确认，不自动重试。

原生访问在框架线程执行。调用前完整读取 SDK 模块接口和虚表，验证实际需要的状态/操作函数位于可执行内存；只访问固定的 SDK 方法，不接受任意地址、窗口名、菜单参数或物品操作。

2026-10-24 实测：旧路径 AgentInterface.Show 返回 opened=true，但 inventory agent 持续为活动、19 个已加载背包相关窗口全部隐藏。修复后先报告关闭，打开返回实际 open=true，并发现可见 InventoryLarge 及相关格子窗口；重复打开 dispatched=false。关闭后所有背包窗口不再可见，状态为 inactive；重复关闭 dispatched=false。没有移动、使用、拆分或丢弃任何物品。

离线 NativeUiReadTest 使用安装 SDK 的真实虚表布局和独立进程内的可执行回调，验证正常背包参数 0、状态读回、幂等性、原生调用不产生状态变化时不能报告成功，以及不可读模块/虚表、不可执行操作指针和未解析状态指针的拒绝路径。该套件在 Debug/Release 均通过 100/100 项；未逐一测试其他背包布局或其他 agent。
