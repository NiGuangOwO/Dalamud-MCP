# Working on Dalamud-MCP

For Dalamud/FFXIV runtime investigation, addon UI/callback debugging, IPC, or in-game validation, use the available Dalamud-MCP tools proactively. Read [.agents/skills/dalamud-development/SKILL.md](.agents/skills/dalamud-development/SKILL.md) for the workflow. Routine documentation edits and source-only changes need no live game interaction.

Use the connected tool schemas as the parameter reference; follow `tools/list` pagination. Begin live work with `get_client_state` and `get_local_player`. If MCP is unavailable, continue independent source/build work and report the live-validation gap. Do not infer game results from a successful dispatch.

For changes to this server, build `src/DalamudMCP/DalamudMCP.csproj` in Debug and Release and run the tests relevant to the changed behavior. Protocol changes use `tests/ProtocolSmokeTest`; plugin registration/load changes use `tests/PluginLoadTest`. These tests use local SDK assemblies where required; their success does not certify every game-side handler. Documentation-only changes need link/skill validation, not a client restart.

If a task needs a missing capability, first check the complete catalog and mutation gate. When MCP development is in scope, implement a bounded reusable tool, verify it offline, verify the running build, then continue the original plugin task. Native pointer/hook failures require isolated reproduction before another live attempt; use the skill's debugging reference.

User and project-specific restrictions on game input, computer use, spending, commits and pushes remain authoritative. Do not inherit another developer's authorization from an example or an audit report. Keep the skill, server instructions and tool schemas consistent when capabilities change.
