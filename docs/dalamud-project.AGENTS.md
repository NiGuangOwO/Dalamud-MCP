# Dalamud runtime development

When a task depends on live FFXIV state, addon UI nodes/callbacks, plugin IPC, or in-game validation, proactively use the connected DalamudMCP server and the `dalamud-development` skill. Do not wait for the user to say "use MCP". Read `.agents/skills/dalamud-development/SKILL.md` when installed in this project, or use the installed user-scoped skill.

Start live work with `get_client_state` and `get_local_player`. Read the available tool schemas and complete catalog; mutating tools may be hidden by configuration. After building a development plugin, use `load_dev_plugin` to verify the running build before test commands. Verify resulting game state after dispatching an action.

If a required capability is missing and the task includes MCP development, improve and validate the tool before continuing the dependent plugin work. If MCP is unavailable, continue independent source/build work and state what could not be tested live. Routine source-only or documentation changes do not require a game session.

Follow this project's and the user's restrictions on game interaction, computer use and publication. Skill installation grants no additional authorization. Keep project-specific commands and restrictions in this project's own instructions.
