# 原生数字输入框

`set_addon_numeric_input` 使用已安装 SDK 的 `AtkComponentNumericInput.SetValue`，设置一个已发现的原生 NumericInput。与文本输入一样，先复制 `list_addon_elements` 返回的 `nodeId`/`componentPath`，用 `dryRun=true` 读取当前值和动态 Min/Max。值必须是 JSON 原生整数且在 Int32 及控件的当前范围内；不会把字符串、布尔或浮点数强制转换，也不会替调用者夹取或绕过上限。

可使用 `expectedValue` 和 `expectedAddonId` 校验观察到的旧状态。重复设置相同数值不再调用 SetValue。结果包含 `before`、实际 `value`、`min`、`max`、`dispatched`、`changed`、`verified` 和 `exactMatch`；预检中的 exactMatch 表示当前值是否已等于目标。只读回数字不等于业务操作完成。

工具受可变更操作开关约束，预检也不例外。框架线程中检查窗口身份及可见/就绪状态、组件路径、NumericInput 类型与完整结构、OwnerNode/OwnerAddon、有效可见性及启用标记。原生 SetValue 必须已解析到可执行地址。修改后重新解析窗口和节点；身份变化返回 verified=false，要求重新观察。

本工具不发送 Enter、不按“确定”、不选择菜单项，也不自行构造 addon callback。原生数值变化的正常通知仍由 SDK/游戏处理。价格、数量或其他控件的业务含义应在操作前通过当前界面确认；本次只验证拆分数量框。

2026-10-24 实测流程：由已验证目标为 66 个 HQ 4级巧力之宝药的物品菜单，选择已发现的“拆分”，进入 InputNumeric。原生范围为 1–65、当前值为 1。预检无输入；设置 2 后界面文本显示 2；再次设置 2 不发送输入；设置 66 及错误 expectedValue 均被拒绝。随后恢复 1，读取显示文本确认，再点击“取消”。弹窗关闭、原物品仍为 66 个，未确认拆分。

离线检查覆盖真实 SDK 数值/上下限布局、所有权、组件类型、不可读指针、反向上下限、负数、Int32 边界、拒绝强制类型转换和动态范围边界。其他数字输入框、不同窗口及原生通知分支未逐一验证。

Debug/Release 构建均为零警告、零错误；NativeUiReadTest 在两个构建上通过 138/138，Release PluginLoadTest 通过 148/148，ProtocolSmokeTest 通过 50/50。核对运行中的 Debug MVID 后，再次验证预检、1→2 的可见文本变化、恢复 1 和取消。目录为 123 个工具，其中 74 个只读、49 个可变更。
