# 通过运行时发现插件功能

测试或操作目标插件时，先通过 MCP 读取已安装版本、命令帮助及实际界面；本仓库不允许读取目标插件本地源码来发现测试入口。Dalamud-MCP 自身代码和已安装 SDK 可用于开发缺失的通用能力。

`list_plugin_commands` 从 `ICommandManager.Commands` 获取当前注册的命令、帮助、`showInHelp` 及处理器程序集名称，不执行处理器。`query` 可匹配命令、帮助和程序集，最多 128 字符；`limit` 为 1–100，默认 30，跟随 `nextCursor` 分页。最多检查 4096 个注册项，达到扫描边界会返回 `scanIncomplete`。单条帮助最多 4096 字符，并报告 `helpTruncated`。处理器程序集是归属线索，共用命令框架可能隐藏实际所有者。

```json
{"query":"Questionable","limit":30}
```

根命令的注册帮助可以进一步指向插件自己的帮助子命令。按运行时帮助读取子命令列表，再操作其支持的功能，不从记忆中的源码推导参数。

`open_plugin_ui` 通过 `IExposedPlugin.OpenMainUi/OpenConfigUi` 调用插件自己公开的入口，要求一个精确匹配且已加载的插件。未加载、重名匹配和不支持的回调会被拒绝，不通过猜测聊天命令降级。该工具属于修改操作，受 mutation gate 控制；`requested=true` 只表示请求已调用，须通过 addon/ImGui 状态核对窗口。

```json
{"pluginName":"Questionable","ui":"main"}
```

## 部族任务读取

`get_tribal_quest_state` 在游戏线程读取当前 SDK 的 `QuestManager`：

- `allowanceRemaining` 来自 SDK 的实际额度查询，不按本机日期推断每日重置。函数未解析时返回 null 和 `allowanceError`。
- `tribes` 使用客户端 `BeastTribe`、`BeastReputationRank` 表解析名称、当前等级、友好度、上限等级和位置 Level 记录；原生结构经过完整可读性检查。未知等级的 `isMaxRank` 为 null。声望状态不代替任务受理条件检查。
- `dailyQuests` 使用 SDK 的日常账本返回任务名称、原始标记、实际受理/当日完成状态和序列。完整 Quest 表行号与插件展示的短任务号可能不同，应以插件自己的运行时显示为准。

日常任务以前完成过，不代表今天无法再次受理。最终可接任务以当前 NPC 菜单为准；完成证据应包含当日完成标记、系统交付消息和友好度实际变化。

验证范围：Debug/Release 构建，NativeUiReadTest 的 SDK 声望索引与未解析额度函数分支，PluginLoadTest 的真实命令注册读取、公开 UI 回调单次调用、缺失回调和 mutation gate 拒绝分支，以及 ProtocolSmokeTest。现场使用运行时帮助找到 Questionable 入口，读取实际 12 次限额，并将第一项骨颌族日常从受理推进至交付，核实友好度增加 70。以上不保证每个第三方插件的全部命令或任务路径。
