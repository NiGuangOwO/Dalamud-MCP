# DalamudMCP 1.0.0.9

同步 GitHub 仓库的规范名称 `NiGuangOwO/DalamudMCP`。插件清单中的 `RepoUrl` 与 `IconUrl`、MCP 服务指引、开发技能、项目约束、README 示例路径和测试提示均已更新；历史发布记录保持原样。

保留 `dalamud-mcp` 作为 MCP server/client 协议标识，以及现有 bridge 可执行文件名。本次不改工具 schema、配置格式或游戏行为。

验证：Debug/Release 构建均为 0 警告、0 错误；ProtocolSmokeTest 50/50，Release DLL 的 PluginLoadTest 138/138，LoadabilityCheck 112/112。发布构建 MVID 为 `ca5e6a0c-bc26-4c18-ad5f-458a50afecbc`，包 SHA-256 为 `9C84CF8F29E356FC5A7F68452CA5F5019A988AED2308012435DD6624452D3B50`。

本次未启动游戏；改名仅涉及仓库元数据、说明文字和客户端指引，未添加需要现场游戏状态验证的行为。