# Native addon dispatch / 原生界面派发地址

`get_addon_native_dispatch` inspects one currently loaded addon without installing hooks or invoking native functions.
Dalamud lifecycle instrumentation can replace a window's vtable with a managed wrapper. Its current `ReceiveEvent`
pointer can therefore point outside the game module and is unsuitable as a game executable RVA.

该工具只读检查当前已加载界面。Dalamud 生命周期监测可能将虚函数表替换为托管包装表，
因此当前 `ReceiveEvent` 指针可能位于游戏模块之外，不能直接作为客户端可执行文件的 RVA。

```json
{"addon":"MiniGameBotanist"}
```

The tool matches the addon address and modified table against at most 1024 entries in the installed lifecycle registry.
Private field names and pointer types are checked before reading; a mismatched SDK layout returns `available=false`.
The original and current handler slots are read with `MemoryProbe`. `receiveEventRva` is supplied only when the
original handler falls inside the current game's main module.

工具最多检查 1024 条生命周期记录，并同时匹配窗口地址与替换表地址。
读取前校验私有字段名称及指针类型；SDK 布局不匹配时返回不可用。
虚函数表槽通过受保护的内存读取取得，只有原始处理器位于当前游戏主模块内时才提供 `receiveEventRva`。

Returned addresses are process/build-specific. Re-query after window replacement, reload or restart, and match the
offline executable to the running client before static analysis. A valid address is not permission to call or hook it.

地址仅适用于当前进程与构建；窗口替换、重载或重启后应重新查询，静态分析前应核对可执行文件版本。
有效地址不代表可以直接调用或安装钩子。

Offline checks use the installed SDK's real lifecycle table type with inert pointer fields. They cover matching,
replacement, missing tables, unknown entries and the search budget without invoking any native delegate.
Live verification on 2026-10-17 resolved `MiniGameBotanist`'s managed wrapper to an original handler inside the game module.

离线检查使用已安装 SDK 的实际生命周期表类型及惰性指针字段，覆盖匹配、替换、缺失、未知条目和数量上限，
不调用原生委托。2026-10-17 现场验证将 `MiniGameBotanist` 的托管包装入口解析为游戏模块内的原始处理器。
