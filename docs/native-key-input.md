# 原生游戏键盘输入

这两个工具用于把键盘输入送进游戏，而不是 Dear ImGui 窗口：

- `press_game_key`：通过 `InputDeviceManager.Instance()->KeyboardDevice->SendKey` 发送一次或多次完整点击。
- `hold_game_key`：通过有界的 Win32 `SendInput` 发送 key-down，保持指定时间，再发送 key-up。

它们要求变更工具门已开启。调用后仍必须回读角色、界面或 addon 状态；成功派发不代表目标系统已经产生业务结果。

## `press_game_key`

```json
{
  "key": "RETURN",
  "repeat": 1,
  "intervalFrames": 2,
  "expectedTerritoryId": 177,
  "activateWindow": true,
  "dryRun": false
}
```

- `key` 使用精确的 FFXIV `SeVirtualKey` 名称，例如 `UP`、`DOWN`、`LEFT`、`RIGHT`、`RETURN`、`SPACE`、`ESCAPE`、`W`、`A`、`S`、`D`、`KEY_1`、`NUMPAD1`、`F1`。
- `repeat` 限制为 1–8；`intervalFrames` 限制为 1–30。
- 当前客户端必须已登录且本地角色已加载；可选的 `expectedTerritoryId` 是区域守卫。
- 原生 `KeyboardDevice.SendKey` 在单次调用中生成 key-down/key-up 对，因此不会留下悬挂按键。
- 调用前会检查目标键的原始键态。键已经按下时拒绝覆盖物理输入。
- 每次点击后等待一个框架 tick，并回读键态；若异常残留非零值，会清除并再次核验。
- 鼠标键和 `PAD_*` 游戏手柄键会被拒绝。

## `hold_game_key`

```json
{
  "key": "W",
  "durationMs": 250,
  "expectedTerritoryId": 177,
  "activateWindow": true,
  "dryRun": false
}
```

- `durationMs` 限制为 50–2000。
- 使用 Win32 `SendInput` 发送一次 key-down，等待指定时间，再由 `finally` 路径发送 key-up；超时、取消和异常也会尝试释放。
- 返回 `keyDownDispatched`、`keyUpDispatched`、`foregroundAtRelease` 和键态前后值。
- 若发送 key-down 后游戏失去前台，key-up 仍会尝试发送，避免操作系统留下悬挂键；这种场景不能宣称目标动作成功。

## 前台窗口守卫

原生输入最终经过 Windows `SendInput`，它发送到当前前台窗口。调用方必须显式传入 `activateWindow: true` 才会尝试切换前台；工具只接受游戏进程自己的窗口，并验证切换后的前台 PID 与当前进程一致。

切换流程使用 `BringWindowToTop`、`SetForegroundWindow` 和 `SetFocus`；受 Windows 前台锁限制时，会仅在调用线程和当前前台线程之间临时 `AttachThreadInput`，随后解除。若始终无法取得前台，工具在发送任何键之前返回错误。

`dryRun` 只验证客户端、区域、窗口和原生键盘，并读取当前键态，不会切换前台或发送输入。变更工具门关闭时，连 `dryRun` 也会在访问客户端之前被拒绝。

## 已验证行为

2026-10-24 在实时中文客户端上：

- `press_game_key` 的只读探测确认 `LEFT` 原始键态为 0，原生键盘设备可用，键态表长度 159。
- 首次未取得前台时，工具拒绝发送，角色位置和朝向保持不变。
- `RETURN` 成功切换原生聊天输入；`ChatLog` 可见节点数为 `40 → 41`，再次按 `RETURN` 后变为 `39`。这证明输入进入游戏原生 UI，而不是只留在 Dear ImGui。
- 单次 `LEFT` 点击的 down/up 在键态上正确闭合，但未使角色朝向变化，说明短点击适合 UI 选择，连续移动应使用 `hold_game_key`。
- `hold_game_key` 将 `W` 保持 250ms 后，角色位置从 `z=4.91` 变为 `z=3.93`，朝向不变；返回的 `rawBefore=0`、`rawAfter=0`、`foregroundAtRelease=true`。

离线验证检查工具注册与 mutation 分类、未知键拒绝、关闭变更门拒绝、Windows x64 `INPUT` 结构大小，以及项目 schema 与协议测试。