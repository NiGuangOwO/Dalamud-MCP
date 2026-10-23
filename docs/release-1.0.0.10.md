# DalamudMCP 1.0.0.10

插件产品名统一为 `DalamudMCP`：插件清单显示名、配置窗口、聊天前缀、中英文本地化、MCP 标题和提示、bridge 诊断信息、README、历史发布标题及相关测试断言均已同步。GitHub 仓库与 raw 图标地址继续使用 `NiGuangOwO/DalamudMCP`。

保留小写 `dalamud-mcp` 作为 MCP server/client 协议标识，保留 `dalamud-mcp-bridge.exe`、`DALAMUD_MCP_*` 环境变量和 `/dalamudmcp` 命令，避免客户端或现有配置失效。本次不改工具 schema 或游戏行为。

验证：Debug/Release 构建均为 0 警告、0 错误；ProtocolSmokeTest 50/50，Release DLL 的 PluginLoadTest 138/138，LoadabilityCheck 112/112，BridgeSmokeTest 22/22。全工作区（包含构建产物）不再存在带空格的旧产品名。发布构建 MVID 为 `9a4c816b-1c4e-4a2e-8a6b-6869ccfa6a88`，包 SHA-256 为 `F682788C4C345E06A697BAC05FA8D1BBF8F3C5BF308C1D230626485BE52C27AE`。

本次未启动游戏；改动仅涉及显示名称、元数据和说明文字，没有需要现场游戏状态验证的新行为。