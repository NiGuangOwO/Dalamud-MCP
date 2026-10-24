# 原生下拉框

下拉框内部的复选框/列表由组件自身接收事件，直接调用 addon 的按钮事件可能只返回派发成功而没有展开列表。单独调用 SDK `SelectItem` 也可能只修改选中文字，不触发业务逻辑。

三个工具配合使用：

1. `list_addon_elements` 查询 `componentType="DropDownList"`，记录精确 nodeId/componentPath。
2. `get_addon_dropdown` 读取当前下标、带下标的选项、`isOpen` 和 `openStateChangePending`。可读取关闭列表，默认每页 40 项，最多 100 项，总数上限 1024。空/不可读文字保留 null，`scanIncomplete=true`。
3. `set_addon_dropdown_open` 明确设置 `open=true`，可先 `dryRun=true`。它校验内层复选框、所属 ULD 与注册的唯一 ButtonClick 接收者，使用 SDK 修改勾选状态，再通知拥有它的下拉组件一次。关闭同理，不选择选项。
4. 重新读取状态，等待 `isOpen=true`、`openStateChangePending=false`，确认实际列表可见。
5. `select_addon_dropdown_item` 传入已观察的 `index`、精确 `expectedLabel`。用 `expectedIndex` 和 `expectedAddonId` 保护旧状态。先预览，再执行。关闭/切换中的列表、禁用项、标签不匹配、身份变化和不明确的事件接收者会被拒绝。
6. 查看实际地图、设置或其他业务状态。`exactMatch` 只证明当前原生选中下标/文字匹配，不代替业务结果。

地图示例（ID 仅为本次观察，不保证下次相同）：

```json
{"addon":"AreaMap","nodeId":10,"max":3}
```

```json
{"addon":"AreaMap","nodeId":10,"open":true,"expectedOpen":false,"expectedAddonId":20}
```

```json
{"addon":"AreaMap","nodeId":10,"index":1,"expectedLabel":"利姆萨·罗敏萨上层甲板","expectedIndex":2,"expectedAddonId":20,"dryRun":true}
```

执行时去掉 `dryRun` 或设为 false。选择路径调用安装 SDK 的 `SelectItem`，确认原生状态达到目标后，再调用 SDK `DispatchItemEvent(ListItemClick)`，要求列表只向所属下拉组件注册了一个该类型事件。工具不猜 addon 回调参数，也不把所有已注册事件全部发送。相同选择、相同展开状态不重复发送输入。选项标签保留原生 SeString 控制数据，必须使用工具返回的准确字符串。

打开/关闭后原生状态可能已变、动画更新仍 pending，此时 `exactMatch=false`；等待后重新读取，不能把派发当作完成。身份变化会让 `verified=false`，需重新发现控件。

验证覆盖：隔离原生 SDK 调用桩、所有权、可见性、禁用项、事件链、函数地址、分页、旧值保护和无转变分支。游戏内地图选项按 11+11 条分页读取；完成预览/拒绝旧值/幂等/开关验证，实际选中地图 12→11→12，玩家区域保持 129。其他窗口及选择造成的业务后果尚未验证。
