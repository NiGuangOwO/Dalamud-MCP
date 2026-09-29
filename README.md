# Dalamud MCP

English | [简体中文](README.zh-CN.md)

Dalamud MCP is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FINAL FANTASY XIV that hosts a
[Model Context Protocol](https://modelcontextprotocol.io) server on a loopback port, giving an AI agent access
to the live state of a running game client. The plugin runs inside the game process, so it reads the same memory
the client itself uses: the object table, the local player, party and alliance, targets, FATEs, currency, Excel
game data, and arbitrary validated raw memory through
[FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).

An MCP client connects to `http://127.0.0.1:18777/mcp` and calls 87 tools — 58 read-only, plus 29 mutating tools
(opt-in) that can act on the game: cast actions, target and move, send chat, manage plugins, drive addon windows,
and register IPC endpoints with other plugins. A background event collector also records game-state changes into
a ring buffer that an agent can poll instead of re-reading full state.

> **Verification scope.** All 33 read-only tools that existed at the time of the single live verification session
> were verified against a logged-in client. Offline suites cover the protocol, the transport, the load path, and
> every shipped tool schema. See [Verification](#verification) and [Limitations](#limitations) for the exact scope.

## Contents

- [Build](#build)
- [Install as a dev plugin](#install-as-a-dev-plugin)
- [Configure](#configure)
- [Connect an agent](#connect-an-agent)
- [Tool reference](#tool-reference)
- [Safety model](#safety-model)
- [Architecture](#architecture)
- [Verification](#verification)
- [Limitations](#limitations)

## Build

The plugin is built with [`Dalamud.NET.Sdk`](https://www.nuget.org/packages/Dalamud.NET.Sdk), the same SDK
[SamplePlugin](https://github.com/goatcorp/SamplePlugin) uses, so the project needs no hand-written references to
the Dalamud assemblies.

```powershell
cd <repo>\src\DalamudMCP
dotnet build DalamudMCP.csproj -p:Platform=x64
```

The SDK resolves the Dalamud installation from the local XIVLauncher install rather than NuGet. On Windows it picks
the global launcher path, so `Directory.Build.props` in the same directory pre-seeds the SDK's documented
`DALAMUD_HOME` override when the CN install is the one present:

- `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
- `%APPDATA%\XIVLauncher\addon\Hooks\dev`

Override it if your assemblies live elsewhere, either by exporting `DALAMUD_HOME` or on the command line:

```powershell
dotnet build DalamudMCP.csproj -p:Platform=x64 -p:DalamudLibPath="D:\some\Hooks\dev\"
```

There is no `.sln`. Only the plugin, `bridge\` and the test projects are built directly; the SDK contributes the
`DalamudPackager` package reference and the Dalamud, `Lumina`, `Newtonsoft.Json` and `FFXIVClientStructs`
references for you. `FFXIVClientStructs.dll` therefore comes from the same `Hooks\dev` directory that Dalamud
ships, which keeps the structure offsets in sync with the installed Dalamud version.

### Packaging

`DalamudPackager` runs after every build and generates the plugin manifest from the properties in
`DalamudMCP.csproj` — there is no hand-maintained `.json` to keep in sync:

| Build | Output |
| --- | --- |
| `Debug` (default) | `bin\x64\Debug\DalamudMCP.json`, written next to the assembly |
| `Release` | the same manifest plus `bin\x64\Release\DalamudMCP\latest.zip`, the plugin-repository package |

```powershell
dotnet build DalamudMCP.csproj -p:Platform=x64 -c Release
```

`latest.zip` contains `DalamudMCP.dll`, `DalamudMCP.json` and `DalamudMCP.deps.json`, which is exactly what a
plugin repository serves. `.github\workflows\build.yml` runs that Release build on every push and pull request and
uploads the zip as a build artifact.

Debug output lands in `src\DalamudMCP\bin\x64\Debug\` and contains `DalamudMCP.dll`, `DalamudMCP.json` and
`DalamudMCP.deps.json`.

## Install as a dev plugin

The build output already contains everything Dalamud needs, so it can be registered in place — no copy step is
required:

```
src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll
src\DalamudMCP\bin\x64\Debug\DalamudMCP.json
src\DalamudMCP\bin\x64\Debug\DalamudMCP.deps.json
```

To install it the way a plugin repository would, unpack `bin\x64\Release\DalamudMCP\latest.zip` into a directory of
its own and point the dev-plugin location at that directory instead.

### Activating in-game

1. Launch the game and use `/xlsettings` in chat or `xlsettings` in the Dalamud Console to open the Dalamud
   settings.
    * Go to `Experimental`, and add the full path to `DalamudMCP.dll` from the build output above to the list of
      Dev Plugin Locations.
2. Use `/xlplugins` (chat) or `xlplugins` (console) to open the Plugin Installer.
    * Go to `Dev Tools > Installed Dev Plugins`; `DalamudMCP` should be visible. Enable it.
3. Once loaded, the plugin starts the listener automatically (`Enabled` and `AutoStart` both default to true).
   Check it with `/dalamudmcp status`.

Step 1 only needs to be performed once; it is preserved afterwards. The plugin can be disabled, enabled or set to
load at startup from the Plugin Installer at any time.

## Configure

Open the settings window with `/dalamudmcp` (or the plugin-installer config button). The window title is
"Dalamud MCP".

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for the listener. |
| `AutoStart with the plugin` | `true` | Start the listener when the plugin loads. |
| `Port` | `18777` | TCP port. Clamped to 1024-65535. Bound to `127.0.0.1` only. |
| `Framework timeout (s)` | `10.0` | How long a tool call waits for the game's framework thread before failing. Clamped to 0.5-120. |
| `Allow mutating tools` | `false` | Whether tools that change game state are visible to and callable by clients. |
| `Auth token` | empty | Shared secret. Empty disables authentication. |
| `Max object results` | `200` | Upper bound on objects returned by one query. Clamped to 1-5000. |
| `Max memory read (bytes)` | `65536` | Upper bound for a single raw memory read. Clamped to 16-1048576. |
| `Log every MCP request` | `false` | Write each JSON-RPC request and its outcome (method, id, tool name, elapsed ms) to the plugin log. Read live, so toggling it applies at once. Off by default because a polling agent can issue many calls per second. |
| `Language` | `auto` | UI and `/dalamudmcp` feedback language. `auto` follows the game client's language; `English` and `简体中文` are also available. Takes effect immediately, no reload needed. |

Port and timeout changes need a listener restart; the window says so and offers a "Restart listener" button. Other
edits take effect immediately. Settings persist when a control loses focus after an edit, so dragging a slider does
not rewrite the config file every frame. The window also offers "Save now", "Start listener" / "Stop listener" /
"Restart listener", and "Open config folder".

The plugin ships English and Simplified Chinese strings (`src\DalamudMCP\Localization.cs`). With the default
`Language: auto`, a Chinese game client shows Chinese labels, command feedback and the bind-failure toast; an
English client shows English. The tables compile into the plugin DLL (no satellite resources) and missing keys
fall back to English.

`AllowMutatingTools` defaults to **off**, so write access is granted explicitly rather than as a side effect of
installing a plugin. The gate is enforced in two places: mutating tools are filtered out of `tools/list`, and
`tools/call` rejects them with an explanatory error even if the client knows the name. Turning the setting on also
changes the label from "read-only (recommended)" to a warning that "agents may change game state".

**Note:** the gate is fully implemented and tested, and the mutating tools ship behind it. The 29 tools listed in
this document — action casting and targeting, auto-movement, teleporting, chat and slash commands, plugin
management, addon-window control, screen capture, and the IPC endpoint registry — can act on the game. All of them
are flagged mutating, so they stay invisible in `tools/list` and reject `tools/call` until `AllowMutatingTools` is
turned on; the other 58 tools remain read-only.

## Connect an agent

Endpoints, all on `127.0.0.1`:

| Endpoint | Method | Purpose |
| --- | --- | --- |
| `/mcp` | POST / GET / DELETE | Streamable HTTP transport. The main endpoint. |
| `/sse` | GET | Legacy HTTP+SSE transport (protocol 2024-11-05). |
| `/messages?sessionId=...` | POST | Legacy transport message sink, paired with `/sse`. |
| `/health` | GET | Status: tool count, protocol versions, session count, whether auth and mutating tools are on. |
| `/tools` | GET | Flat tool catalog (name, title, description, mutating flag). |
| `/` | GET | Server info and the transport map. |

The server advertises protocol revisions `2025-06-18`, `2025-03-26` and `2024-11-05`.

**`Mcp-Session-Id` is required after `initialize`.** Only the `initialize` request may be sent without it. Every
later POST to `/mcp` must carry the `Mcp-Session-Id` header returned by the handshake, or the server answers `400`
with `{"error":"session_required"}`. An id the server no longer knows returns `404` with
`{"error":"session_not_found"}` so the client re-initializes instead of looping. A conforming MCP client handles
this automatically.

If an auth token is set, every request must carry it — including `/health` and `/tools`. Only `OPTIONS` (CORS
preflight) and `GET /` are answered without it:

```
Authorization: Bearer <token>
```

A `?token=<token>` query parameter is accepted as an alternative, which is convenient for a browser tab or a
client that cannot set headers.

A generic MCP client configuration for an HTTP-capable client looks like this — consult your client's
documentation for the exact key names, since they differ:

```json
{
  "mcpServers": {
    "ffxiv": {
      "type": "http",
      "url": "http://127.0.0.1:18777/mcp",
      "headers": {
        "Authorization": "Bearer <token-if-you-set-one>"
      }
    }
  }
}
```

The `headers` block can be omitted entirely when no auth token is set.

### stdio-only clients

Some clients launch a subprocess and speak JSON-RPC over stdin/stdout instead of HTTP. This repository ships a
bridge for that case: `bridge\DalamudMcpBridge`. It is a small, dependency-free console app that reads
newline-delimited JSON-RPC on stdin and forwards each message to the plugin's HTTP endpoint, so point the client's
`command` at the **bridge**, never at the plugin DLL.

```powershell
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
```

Then configure the client with the built executable:

```json
{
  "mcpServers": {
    "dalamud": {
      "command": "C:\\path\\to\\Dalamud-MCP\\bridge\\DalamudMcpBridge\\bin\\Release\\net10.0\\dalamud-mcp-bridge.exe"
    }
  }
}
```

The bridge takes `--port <n>`, `--url <http://host:port>`, `--token <token>` and `--verbose`, and also reads
`DALAMUD_MCP_PORT`, `DALAMUD_MCP_URL` and `DALAMUD_MCP_TOKEN` from the environment, so the flags can stay out of
the client config. It resolves to `http://127.0.0.1:18777` when nothing is given.

Two things it handles that a raw pipe would not:

- **Session capture.** The plugin issues a session id in the `Mcp-Session-Id` *response header* during the
  handshake. A stdio client only ever sees the JSON body, so it could never observe or echo that id. The bridge
  captures it and attaches it to every later request.
- **Re-handshake.** If the game or plugin reloads, the session id goes stale. The bridge detects the
  `session_required` rejection, mints a fresh session with its own `initialize`, replays the handshake
  notification, and retries the original request once — so a reload does not require restarting the client.

It also fails fast and loudly on an auth mismatch. Before it opens the stream it probes `GET /health`, and a
non-success answer is fatal rather than something to carry on past. A `401` states whether the problem is a missing
token or a wrong one, and names the fix:

```
[bridge] the plugin requires a bearer token and none was supplied.
[bridge] pass --token <token> (or set DALAMUD_MCP_TOKEN) to match the plugin's AuthToken setting.
```

stdout carries protocol messages only; all diagnostics go to stderr, so `--verbose` is safe to leave on.

Verify the server is up before wiring a client:

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
Invoke-RestMethod http://127.0.0.1:18777/tools | ConvertTo-Json -Depth 4
```

## Tool reference

87 tools: 58 read-only plus 29 mutating tools (control, chat, plugin management, addon windows, and the IPC
endpoint registry) that are hidden until `AllowMutatingTools` is on. Names are exactly as they appear in
`tools/list`.

### Client and session

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_client_state` | Login status, zone/territory (id and name), map, instance, language, PvP/PvP-excluding-Den/GPose flags, idle state, the blocking condition flag, whether the local player object is loaded, framework update delta, last update time, process id and pointer size. The recommended first call, since most other tools report nothing useful while logged out. | none |
| `get_local_player` | The character you control: identity, world, job, level, HP/MP, position, statuses, plus account-level identity from `PlayerState` (content id, home/current world, race, tribe, sex, effective level, level-sync and grand company). Returns `loggedIn=false` when logged out, or `loggedIn=true` with `playerLoaded=false` while the character is still loading. | none |
| `get_player_attributes` | Character-sheet attributes via `IPlayerState.GetAttribute`, plus base Strength/Dexterity/Vitality/Intelligence/Mind/Piety. Non-zero attributes only. | none |
| `get_job_levels` | Level, experience and unlocked flag for each class and job, marking which is current. Jobs that are neither unlocked nor levelled are skipped. | none |
| `get_conditions` | Every `ConditionFlag` currently true, plus explicit booleans for `inCombat`, `mounted`, `crafting`, `gathering`, `betweenAreas`, `watchingCutscene`, `occupiedInEvent` and `boundByDuty`. | none |
| `get_currency` | Gil, FC gil, seals, allied seals, wolf marks, gold saucer coins, retainer gil, weekly tomestones, plus named currency-container items (tomestones, scrips, clusters). | none |
| `get_titles_and_achievements` | Pass `titleId` or `achievementId` to inspect one entry (name, description, points/completion for achievements; name, prefix flag, unlocked for titles). With neither, returns sheet counts, whether the achievement/title lists are loaded, and a hint. | `titleId`, `achievementId` |
| `get_unlock_state` | Whether a specific thing is unlocked. | `type` (required, 23 values: `mount`, `minion`, `emote`, `orchestrion`, `tripleTriadCard`, `recipe`, `instanceContent`, `action`, `generalAction`, `craftAction`, `trait`, `buddyAction`, `buddyEquip`, `glasses`, `ornament`, `title`, `howTo`, `quest`, `leve`, `achievement`, `aetherCurrent`, `classJob`, `unlockLink`), `id` (required) |
| `get_aetherytes` | Teleport destinations with gil cost, territory and favourite status. | `max` (default 200) |

### Game objects

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_game_objects` | Objects loaded around you: players, NPCs, enemies, minions, mounts, treasure, aetherytes. | `objectKind`, `nameContains`, `maxDistance`, `detailed` (default true), `max` (default 200) |
| `find_game_object` | One object by id or exact name, plus alternatives sharing the name. | `entityId`, `gameObjectId`, `name` |
| `get_party` | Party or alliance members with jobs, levels, HP/MP, world, territory and statuses. | none |
| `get_targets` | Current, focus, soft, mouseover, mouseover-nameplate, previous and GPose targets. | none |
| `get_fates` | Active FATEs with level range, progress, time remaining and position. | `fateId` |
| `get_nearby_enemies` | Battle NPCs within a radius, sorted by distance, alive only. | `radius` (default 30), `max` (default 50) |
| `get_object_table_info` | Raw object-table addresses and per-array entry counts. | none |

### Inventory and companions

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_inventory` | The player's carried inventories (the four main bags), each slot with item id, name, quantity and slot index. Results are capped at 200 entries across all containers. | none |
| `get_equipment` | Currently equipped gear, per slot: item id, name, quantity, condition (durability %) and spiritbond/collectability. | none |
| `get_buddy_list` | Companion buddies: chocobo and pet/trust buddies with entity id, data id, HP/MP and the resolved game object when present. | none |
| `get_duty_state` | Whether a duty is currently started, plus the content-finder condition (name, id) when one is bound. | none |

Tools that need a loaded character return `{"available": false, "reason": "not logged in"}` rather than failing
when you are at the title screen, so a call is always safe.

### Game data (Excel)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `search_game_data` | Text search across a sheet's searchable text columns (Item, Action, Status, Quest, Mount, TerritoryType, World, ClassJob, ...). Use `get_game_data_sheet_info` first to see which columns are searchable. | `sheet` (required), `query` (required), `exact` (default false), `max` (default 25, max 200) |
| `get_game_data_row` | One row by id, returning every readable column (capped at 120 columns per row; `ExcelPage` and `RowOffset` are skipped, empty values omitted). Row references come back as `{"rowId": n}`, not expanded. | `sheet` (required), `rowId` (required) |
| `list_game_data_sheets` | Sheet names, optionally filtered. Over a thousand exist, so filter. | `filter`, `max` (default 200, max 2000) |
| `get_game_data_sheet_info` | Sheet name, full row type, whether the client loaded the sheet, row count, column count with index/type/offset, the searchable text columns, and the property count. | `sheet` (required) |
| `get_item` | Item name, singular, description, item level, equip level, rarity, item action, UI/search category, class-job category, stack size, HQ flag, untradable flag, mid/low vendor price, collectable flags. | `itemId`, `name` (exact unless `contains`), `contains` (default false) |
| `get_action` | Action name, description, job abbreviation, class-job level, cast/recast times (in 100 ms units), range, effect range, primary/secondary cost type and value, category, target area, PvP and player-action flags. The description lives on the separate `ActionTransient` sheet and may be absent. | `actionId`, `name` (exact) |
| `get_status` | Status name, description, icon, max stacks, FC-buff flag, category, dispel/gaze/permanent flags, party-list priority and the movement/action locks. | `statusId`, `name` |
| `get_territory` | Zone name, place name, zone and region names, map, content-finder condition, aetheryte, expansion, PvP-zone flag and mount/stealth permission. | `territoryId`, `name` (case-insensitive substring) |
| `get_class_job` | Class/job definitions: name, abbreviation, English name, category, starting level, duty-queue flag, soul crystal and unlock quest. Omit the id to list all — the cheapest way to map a job id to a name. | `classJobId` |

### Raw memory

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `read_memory` | Read an absolute address as a hex+ASCII dump or a scalar. Validated against the OS region map first. Hex dumps are capped at 4096 bytes. | `address` (required, hex string), `format` (default `hexdump`), `length` (default 256 hexdump / 1024 string / format size) |
| `query_memory_region` | OS-reported state for addresses: committed, readable, writable, executable, protection, owning region. With `length`, walks up to 64 regions so a buffer crossing page boundaries is fully described. | `address` (required), `length` |
| `read_pointer_chain` | Follow a multi-level pointer from a static base: dereference, add offset, repeat, optionally decoding a value at the end. Returns every intermediate address so a broken step is obvious. | `address` (required), `offsets` (required, max 16 levels), `valueFormat` |
| `get_module_info` | Game module name, base and size, Dalamud's search base, whether the scanner runs on a copy, the `.text` / `.data` / `.rdata` section bases and sizes, and the section offsets so you can tell whether this build reports them absolute or relative. | none |
| `scan_signature` | Byte-pattern scan with `??` wildcards via Dalamud's signature scanner. | `signature` (required), `section` (`text` default, `data`, `rdata`, `module`), `maxResults` (default 1, 0 for all) |
| `read_object_memory` | Read at a game object's address plus an offset — the bridge from typed object tools to raw bytes. Resolve the object by `entityId`, `objectIndex`, `localPlayer`, or pass an absolute `address` directly. Negative offsets are allowed for walking backwards inside a struct. | `entityId`, `objectIndex`, `localPlayer`, `address`, `offset`, `length` (default 64), `format` |

### Direct structs (FFXIVClientStructs)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_job_gauge` | The live job gauge read straight from `JobGaugeManager`'s memory, for gauge state Dalamud's managed API does not expose (no gauge accessor is among the injectable services). Reports `classJobId`, then locates the dedicated gauge struct for that job inside the manager's union and decodes every `[FieldOffset]` field — including `BitFieldAttribute` bit ranges, rendered as named bits — plus the raw bytes. Jobs without a dedicated gauge (Arcanist, Rogue, Blue Mage) return `hasDedicatedGauge=false`. | none |
| `get_active_statuses` | The raw 60-slot `StatusManager` of a BattleChara, read at its struct offset rather than through Dalamud's `StatusList`: owner address, the extra-flags byte, the special-status timer/direction float, and every status entry (id, param, remaining time, source object id, sheet name/description/stacks). Resolve the target like `read_object_memory`, or pass an absolute `address`. | `address`, `entityId`, `objectIndex`, `localPlayer`, `max` (default 60) |

Both tools degrade the same way everywhere else in this plugin: if FFXIVClientStructs' static address resolver has
not initialized (the plugin loaded before the game was ready) the tools return `{"available": false, ...}` with a
reason, never a thrown exception.

Valid `format` values for `read_memory` and `read_object_memory`: `hexdump`, `bytes`, `u8`, `u16`, `u32`, `u64`,
`i8`, `i16`, `i32`, `i64`, `f32`, `f64`, `bool`, `string`, `utf16`, `pointer`.

Addresses are emitted and accepted as hex strings such as `"0x7FF6A1B2C3D4"`, because JSON numbers lose integer
precision past 2^53 and 64-bit pointers routinely exceed that. The parser also accepts a trailing `h`, underscores
and bare decimal, but passing a quoted `0x` string is the reliable form.

### UI (mutating, opt-in)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `open_addon` | Opens an in-game addon/system window through `AgentInterface.Show()` on the agent that owns it — the same path the game's own UI uses. The addon must be named from a fixed allowlist of ~66 agents (inventory, armoury, emote list, quest journal, achievements, mount/minion notebooks, orchestrion, teleport, duty finder, gear sets, config, currency, retainer, free company, and more); unknown or non-openable names are rejected rather than guessed. A freshly created window ignores synthetic clicks until the game refreshes it, so `open_addon` also schedules a second `Show()` ~1.2 s later. | `addon` (required, enum of allowlisted names) |
| `close_addon` | Closes one of the same allowlisted addon windows through `AgentInterface.Hide()`. Closing a window that is not open is a no-op, not an error. | `addon` (required, enum) |
| `get_addon_state` | Read-only census: which of the allowlisted agents are currently active. Reports `active` and `inactive` name lists. | none |
| `list_addon_elements` | Walks a loaded addon's node tree (DFS from its root node) and reports each node's id, type, label, size, screen position and whether it is clickable — the discovery half of UI automation. Component nodes (buttons, lists, drop-downs) report their runtime composite type. | `addon` or `addonId`, `maxDepth` (default 12), `maxNodes` (default 100) |
| `click_addon_element` | Dispatches one Atk UI event to a node inside a loaded addon through the addon's own `ReceiveEvent` — the same dispatch the real input pipeline feeds. Event types: `click` (MouseClick), `doubleClick`, `buttonClick` (ButtonClick 25), `buttonPress` (23), `buttonRelease` (24), or `registered` (fires every handler the node has registered). A real click is a press+release pair, so closing a window is `buttonPress` followed by `buttonRelease`. | `addon` or `addonId`, `nodeId` or `index`, `event`, `param` (advanced override) |
| `get_addon_strings` | Reports the string and scalar values an addon currently carries in its `AtkValue` table — the text a menu, dialog or list is displaying right now. Each entry is index, type and decoded value; control characters are escaped so the raw payload stays visible. Read-only. | `addon` or `addonId`, `maxValues` (1-500, default 200), `stringsOnly` |
| `select_addon_menu_item` | Picks an entry from a menu-style addon (one whose options live in its `AtkValue` table) by label, and activates it by firing the addon's callback with that entry's index — the same call the game makes when the option is clicked. Matching is exact after normalization unless `containsMatch` is set. | `addon` or `addonId`, `label` or `index`, `containsMatch`, `dryRun` |
| `probe_receive_event_enable` | Diagnostic: swaps one loaded addon's `ReceiveEvent` vtable slot so every UI event it receives — real or synthetic — is captured before being forwarded. Use with `probe_receive_event_dump` to compare what a real click delivers versus what `click_addon_element` sends. | `addon` or `addonId` |
| `probe_receive_event_enable_listener` | Same capture, but hooks a specific node's first registered listener instead of the addon itself (real component clicks land on node-registered listeners). | `addon` or `addonId`, `nodeId` (required) |
| `probe_receive_event_disable` | Restores the hooked vtable slot. | none |
| `probe_receive_event_dump` | Returns every captured `ReceiveEvent` call: event type, param, and the raw event/event-data buffers decoded at their FCS offsets. Read-only. | none |
| `probe_callback_enable` | Diagnostic: hooks `AtkUnitBase.FireCallback` globally (the game's own addon callback entry, resolved from its call-site signature) so every callback the game performs is recorded with its decoded `AtkValue[]` arguments. Use with `probe_callback_dump` to learn which `(addon, values)` payload reproduces an action. Mutating. | none |
| `probe_callback_disable` | Unhooks the FireCallback probe. Mutating. | none |
| `probe_callback_dump` | Returns every captured callback: addon name, value count, each `AtkValue` decoded by type (Int/UInt/Bool/Float/String/...), the close/update-visibility flag, and the return value. Read-only. | none |

The mutating tools in this set are `open_addon`, `close_addon`, `click_addon_element`,
`select_addon_menu_item`, and the five `probe_*` tools that install hooks. `get_addon_state`,
`list_addon_elements`, `get_addon_strings`, `probe_receive_event_dump` and `probe_callback_dump` are read-only.
Failures (agent module not initialized, agent not openable) return structured
`{"available": false, "reason": ...}` instead of throwing, like the rest of the tool set.

Two operational notes from live testing: `open_addon currency` and `close_addon currency` open and close the
Currency window as expected. `get_addon_state` reports `AgentInterface.IsAgentActive`, which does not flip for
every agent when its window shows — a window can be visibly open while its agent still reports inactive, so the
census is a hint rather than ground truth and `open_addon`/`close_addon` are the reliable operations. A synthetic
`buttonPress`+`buttonRelease` pair on the Currency window's close collision node closes the window; real mouse
clicks reach component-registered listeners rather than the addon's vtable, which is why the `registered` event
mode and the listener-level probe exist.

### Character control (mutating, opt-in)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `execute_action` | Casts an action through `ActionManager.UseAction`. Resolve the action by id, or by exact name from the Action sheet. Targets the self target id (`0xE0000000`) unless `targetObjectId` is given. With `dryRun` it only asks the game whether the action could be used right now. Optional job/territory guards refuse the call when the character does not match. | `actionId`, `actionName`, `targetObjectId` (default self), `dryRun`, `requiredClassJobId`, `requiredTerritoryId` |
| `use_duty_action` | Fires one of the two duty actions (the extra buttons a duty grants), addressed as slot 1 or 2. Refused when the duty grants none. | `slot` (required: 1 or 2) |
| `use_general_action` | Fires a general action by id (sprint, jump, auto-run, dismount, accept raise, ...). | `actionId` (required) |
| `set_target` | Sets the hard target by object id, or by name (exact match first, then contains). | `targetObjectId`, `targetName` |
| `set_focus_target` | Sets the focus target the same way; with no arguments clears it. | `targetObjectId`, `targetName` |
| `interact_with_target` | Interacts with the current hard target through `TargetSystem` (talk to NPCs, open chests, interact with objects). | none |
| `dismount` | Dismounts (general action); reports a note instead of firing when not mounted. | none |
| `cancel_cast` | Cancels the current cast through the hotbar module. | none |
| `accept_raise` | Accepts a raise (return/raise prompt). | none |
| `toggle_sprint` | Toggles sprint. | none |
| `jump` | Jumps. | none |
| `toggle_autorun` | Toggles auto-run. | none |
| `face_target` | Turns the character toward the current target. | none |

All thirteen are mutating and hidden until `AllowMutatingTools` is on. Tools that need a logged-in character
return the standard `{"available": false, "reason": "not logged in"}` otherwise.

### Movement (mutating, opt-in)

Auto-movement follows a real navigation mesh through the [vnavmesh](https://github.com/awgil/ffxiv_navmesh)
plugin's IPC channels; both tools refuse with a phrased error when it is not installed and loaded, because no mesh
means no path. Reading the destination, not the walk, is the agent's job — use `get_game_objects` to pick an id or
name first.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `move_to_entity` | Starts auto-movement toward a game object, snapping the destination to the floor mesh first. | `gameObjectId` (required, decimal or `0x` hex), `allowFlight` |
| `move_to_nearby_targetable_object` | Finds the closest targetable object whose name contains the given text and starts auto-movement toward it. Player characters are skipped unless `includePlayers` is set. | `name` (required), `maxDistance` (default 30), `includePlayers`, `allowFlight` |

### Travel (mutating, opt-in)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `teleport_to_aetheryte` | Teleports to an unlocked aetheryte by id or by name, through `Telepo`, at the same gil cost the in-game teleport menu would charge. Names are matched loosely — case, punctuation and leading articles are ignored, and a bare zone name matches its main city aetheryte — with `territoryId` available to break a tie. | `aetheryteId`, `name`, `territoryId`, `subIndex` |

### Chat and plugin management (mutating, opt-in)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `send_chat` | Sends a chat line through the game's own chat-box entry processor. Channels: `say`, `yell`, `shout`, `party`, `alliance`, `fc`, `tell` (requires `target`), `echo`. The message is UTF-8 encoded with a 500-byte cap enforced before it reaches the game. | `message` (required), `channel` (default `say`), `target` |
| `manage_plugin` | Loads, unloads, reloads, or reports on third-party plugins through their chat commands. `reload` disables then re-loads after a short delay. Plugin names are restricted to letters, digits and `._-`; DLL paths must be absolute, exist, and end in `.dll`. Managing this plugin itself is refused. | `action` (required: `load`/`unload`/`reload`/`all`), `pluginName`, `filePath` |
| `slash_command` | Runs any game or plugin slash command exactly as if it had been typed into the chat box, e.g. `/duty finder`, `/target`, `/pcmd move`, `/vnav moveto`. The command is first offered to Dalamud's command manager, so game and plugin commands resolve the same way they do for a user; only if that declines is it submitted through the chat box. Use `send_chat` for ordinary chat and this for commands. | `command` (required, must start with `/`, max 500 UTF-8 bytes) |

### Plugin bridge (IPC)

Lets the agent talk to other plugins and receive pushed data from them, over Dalamud's standard IPC.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `query_push_data` | Reads entries another plugin pushed to this plugin's `PushData` channel (a 2000-entry ring buffer), newest first, optionally filtered by key. | `key`, `count` (default 50) |
| `register_ipc_endpoint` | Declares that another plugin exposes a callable endpoint, persisted in the config across restarts. | `pluginName`, `methodName`, `signature` (required: `Func<bool>`, `Func<string>`, `Func<int, string>`, `Action<bool>`, `Func<bool, string>`), `description` |
| `call_plugin_ipc` | Invokes a previously registered endpoint. `Func<bool>`/`Func<string>` take no arguments; the others require `arguments.value`. Calling an unregistered endpoint is an explicit error, not a guess. | `pluginName`, `methodName`, `arguments` |
| `list_ipc_endpoints` | Registered endpoints, optionally filtered by plugin. | `pluginName` |
| `plugin_data_subscribe` | Retains the payloads a plugin pushes over the push channel under one key, in a dedicated queue that `plugin_data_poll` drains incrementally. Payloads that arrive while nothing is subscribed are visible only through `query_push_data` and are lost to the rolling buffer once it wraps. | `key` (required), `capacity` (16-10000, default 1000) |
| `plugin_data_poll` | Drains retained payloads for a subscribed key, oldest first. Drained entries are removed, so each payload is delivered once; the rest stay for the next poll. Reports `no_data` when the queue is empty. | `key` (required), `maxItems` (1-10000, default 200) |
| `plugin_data_unsubscribe` | Stops retaining payloads for a key and discards anything still queued for it. | `key` (required) |

Only `register_ipc_endpoint` is mutating; reading pushed data, managing subscriptions, invoking a registered call,
and listing endpoints are read-only.

### Event collector

A background collector ticks with the framework and records game-state changes into a 2000-entry ring buffer, so an
agent can poll "what changed since last time" instead of re-reading full state every turn. Events: `hp_change`,
`mp_change`, `gp_change`, `player_move`, `job_change`, `target_change`, `focus_target_change`, `target_hp_change`,
`combat_damage`, `combat_start`, `combat_end`, `map_change`, `mount_change`, `duty_update`, `fate_update`,
`nearby_enemy`, `nearby_player`. The first frame after login establishes a baseline and emits nothing, and
per-type throttling (default 500 ms) keeps the buffer calm.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `query_events` | Buffered events, newest first, optionally filtered by type and timestamp window. | `types`, `count` (default 50, max 500), `since`, `before` (epoch ms) |
| `configure_event_collection` | Updates what the collector records (player stats, target stats, object ranges, combat/system events, throttle), validated then persisted. Omitted fields keep their current value. | `config` (partial) |
| `get_event_config` | The current collection configuration and buffer occupancy. | none |
| `events_wait` | Blocks until a matching event is recorded, or the timeout elapses, then returns the new events oldest first. Pass `afterId` from a previous reply's `lastId` to wait for something newer; `timedOut=true` means nothing arrived in time. This is the one tool that does not run on the framework thread — a wait that occupied it would stall the client. | `afterId`, `types`, `count` (1-500, default 100), `timeoutMs` (1-30000, default 10000) |

All four are read-only — the collector only observes.

### Chat log

Chat lines the client receives are captured into a 1000-entry ring buffer as they arrive, so an agent can read what
was said instead of scraping the screen. Lines carry the channel (`XivChatType` name), sender, message, relation
kinds and both timestamps.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_chat_log` | Returns captured chat lines, oldest first, filterable by chat type, sender or message substring. Pass `afterId` from a previous reply to fetch only newer lines; the reply's `lastId` is the cursor for the next call. | `count` (1-500, default 100), `chatType`, `sender`, `contains`, `afterId`, `since`, `before` |

### Quests

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_quest_status` | Reports whether a quest is accepted or complete and which sequence step it is on, looked up by id or by (partial) name. Name lookups search every installed client language, so a quest can be found by its English name on a non-English client. | `questId`, `query`, `maxResults` (1-20, default 8) |
| `get_available_quests` | Lists quests currently offered in the world but not yet accepted, with map-marker position, level and objective id — read from the game's own unaccepted-quest marker list. | `nameContains`, `maxResults` (1-20, default 8) |

### Screen capture

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `capture_game_screenshot` | Captures the game as a PNG and returns it base64-encoded. `client` grabs only the 3D scene, `window` includes the title bar and borders. The capture is taken from the window's own surface, so the client may be in the background. With `save=true` the file is also written into the plugin's `captures` folder, and expired files are pruned on the next save. | `area` (enum: `client`/`window`, default `client`), `save`, `ttlSeconds` (60-86400, default 600), `maxDimension` (256-3840, default 1920) |

### Installed plugins

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `plugin_list` | Lists the plugins Dalamud reports, with load state and a manifest summary, paged by cursor. | `query`, `cursor`, `limit` (1-100, default 50) |
| `plugin_describe` | Reports one plugin's manifest in full: author, description, version, repository, tags and load state. | `pluginName` (required) |

## Safety model

**Read-only by default.** 58 of the 87 tools read state; none writes it. The mutating gate is enforced, and the 29
tools registered behind it — action casting, targeting and movement, chat, plugin management, addon-window control
and the IPC endpoint registry — stay invisible until `AllowMutatingTools` is turned on.

**Loopback only.** The listener binds `IPAddress.Loopback` (`127.0.0.1`) directly with a `TcpListener`, not
`HttpListener`. That avoids the HTTP.SYS URL-ACL requirement — no elevation and no `netsh` reservation is needed —
and means the server is never reachable from the network. Do not expose the port through a proxy or port-forward;
the server has no TLS and no protection against that.

**The auth token is the only access control.** Within a loopback binding, any local process can reach the port, so
if the token is empty you are trusting every program on the machine. Set one if that matters. The gate covers every
endpoint that returns data or acts on the game; the only exceptions are `OPTIONS` (CORS preflight) and `GET /` (a
fixed transport advertisement naming the URLs, with no game data and nothing that varies with your configuration),
which are answered without it by design. Everything else, including `/health` and `/tools`, requires the bearer
header when a token is set.

**Raw memory reads cannot crash the client.** An agent can ask for an arbitrary address, and a bad pointer
dereference inside the game process would be an access violation that takes the whole client down. Before any
read, `MemoryProbe` calls `VirtualQuery` and refuses unless the page is committed and readable:

```csharp
var readable = mbi.State == MemCommit && (protect & PageNoAccess) == 0 && (protect & PageGuard) == 0;
```

Reads are then clamped to the end of the region, so a request that runs off the edge returns the readable part
instead of faulting, and the copy itself goes through `ReadProcessMemory` against `GetCurrentProcess()`. Unreadable
addresses produce a normal tool error describing the problem.

**Every tool call runs on the framework thread.** Game memory is only stable while the client's update loop is
running, so `GameThread` marshals each handler onto it via `IFramework.RunOnFrameworkThread`. A bounded timeout
prevents a wedged or zoning client from hanging an HTTP request forever; on timeout the call fails with a message
naming the reason ("The client may be loading, zoned, or busy") rather than blocking.

## Architecture

The path of one tool call:

```
MCP client
  -> POST http://127.0.0.1:18777/mcp        (JSON-RPC 2.0 over HTTP)
  -> MiniHttp         parses HTTP/1.1 by hand on a TcpListener; no HTTP.SYS, no ASP.NET
  -> McpServer        routes, enforces auth and session, dispatches JSON-RPC methods
  -> GameThread       IFramework.RunOnFrameworkThread + timeout
  -> tool handler     one of the eighteen Tool* classes, called with the JSON arguments
  -> GameServices     Dalamud services (IObjectTable, IPartyList, IPlayerState, IDataManager, ...)
     + FFXIVClientStructs   direct reads of the client's own structures
  -> JSON result       serialized back as MCP text content
```

`MiniHttp` and `McpServer` have no Dalamud dependency, which is what lets the smoke test compile and drive them
outside the game. Everything above `GameThread` is transport and protocol; everything below it needs a live client.

Registration is explicit rather than reflection-based. `Plugin` builds the object graph and calls `Register` on
each tool class against a shared `ToolRegistry` — client, objects, game data, raw memory, structs, UI, inventory,
buddies, control, chat, plugin bridge and events — whose `AllowMutating` delegate reads
`Configuration.AllowMutatingTools`, so the gate is evaluated per call rather than cached at startup.

### Commands

| Command | Effect |
| --- | --- |
| `/dalamudmcp` | Open the settings window. |
| `/dalamudmcp start` / `stop` / `restart` | Control the listener. |
| `/dalamudmcp status` | Report running state and the endpoint. |
| `/dalamudmcp port <1024-65535>` | Change the port and restart if running. |
| `/dalamudmcp tools` | Print every registered tool name. |

## Verification

The suites below run without a game client and exercise the shipped artifacts rather than a reimplementation.
They all share one design seam: `src\DalamudMCP\Mcp\McpServer.cs`, `MiniHttp.cs`, `ToolRegistry.cs` and `Json.cs`
deliberately have no Dalamud dependency, so they can be compiled into a host and driven over real loopback HTTP.

| Suite | Result | Covers |
| --- | --- | --- |
| `tests\ProtocolSmokeTest` | `47/47` | Transport and protocol against the real sources. |
| `tests\BridgeSmokeTest` | `22/22` | The stdio bridge against the real server. |
| `tests\LoadabilityCheck` | `110/110` | The built DLL and the manifest generated from the csproj, plus the offset and localization audits. |
| `tests\PluginLoadTest` | `99/99` (`93/93` without game data) | The shipped `Plugin` constructor executed out of game. |
| `tests\McpInterop` | `12/12`, `2/2`, `11/11` | The official MCP SDK as an independent client, plus meta-schema validation. |

```powershell
cd <repo>\tests\ProtocolSmokeTest;   dotnet run --project ProtocolSmokeTest.csproj -p:Platform=x64
cd <repo>\tests\BridgeSmokeTest;     dotnet run --project BridgeSmokeTest.csproj -p:Platform=x64
cd <repo>\tests\LoadabilityCheck;    dotnet run --project LoadabilityCheck.csproj
cd <repo>\tests\PluginLoadTest;      dotnet run --project PluginLoadTest.csproj -p:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\McpInterop\run-interop.ps1
```

Build the plugin and the bridge before running the suites that consume them: `LoadabilityCheck` and
`PluginLoadTest` look for `src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll`, and `BridgeSmokeTest` looks for the newest
`dalamud-mcp-bridge.exe` under `bridge\DalamudMcpBridge\bin`.

### Protocol and transport

`tests\ProtocolSmokeTest` starts the server on an ephemeral port and drives it over real loopback HTTP. It covers
the `initialize` handshake and the negotiated protocol version, `tools/list` with schemas and annotations,
`tools/call` for success and both error paths (`ToolException` and an unexpected exception), unknown-tool and
unknown-method rejection, the mutating-tool gate (refused while closed, revealed and allowed once opened), auth
both on and off (bearer header and `?token=` query), refusal of a `POST /mcp` with no session, 24 concurrent calls
each returning their own result, the opt-in request log, and clean start/stop/restart behaviour.

`tests\BridgeSmokeTest` hosts the real `McpServer` in-process, launches the built `dalamud-mcp-bridge.exe` as a
child process, and speaks newline-delimited JSON-RPC to its stdin. It covers the handshake and session capture,
notification suppression, `tools/list`, argument and error round trips, survival of a malformed line, five
pipelined requests each getting their own id, and a clean exit on stdin EOF. It also runs the bridge twice more
against a server whose token gate is switched on: once **without** `--token` (the bridge must refuse to start and
say the token is missing) and once **with** it.

`tests\McpInterop` drives the real server with the **official** MCP SDK (`@modelcontextprotocol/sdk`, the
TypeScript implementation, pinned at `1.30.1`) rather than hand-written JSON-RPC, so the framing is judged by an
independent implementation. The official client completes `initialize`, reads back the server's identity, parses
`tools/list`, round-trips a tool call, receives a tool error as `isError` rather than a transport failure, and
keeps working afterwards. Its fourth phase validates all 87 **shipped** schemas — `PluginLoadTest` dumps its
`tools/list` payload when `DALAMUD_MCP_DUMP_TOOLS` is set, and that payload is run through the SDK's declared
`ToolSchema` and then compiled against the JSON Schema 2020-12 meta-schema using the `ajv` bundled inside the SDK.

Two findings are worth recording, since both are cases where a check had to be strengthened before it meant
anything:

- The SDK's own `ToolSchema` validation is too lenient to catch this project's defect class: it types
  `inputSchema` as an object with a `"type"` of `"object"` and a `properties` record whose values are only checked
  to be *objects*, never that a property's `type` is one of JSON Schema's legal names. Re-injecting the original
  `{"type":"array of integer"}` defect into the real 87-tool payload still passed it. The label therefore says
  only what it proves, and the ajv meta-schema pass — added alongside it — rejects the defect with an independent
  message: `type must be JSONType or JSONType[]: array of integer`.
- `negative.mjs` points the official client at a deliberately non-conforming server and requires it to fail. It
  once passed for the wrong reason: the host had bound an ephemeral port in the WHATWG list of ports Node's
  `fetch` refuses before connecting (`1719`), so `fetch failed` was being scored as a protocol rejection. The host
  now re-binds until it lands outside the blocked list, `interop.mjs` refuses such a port with a diagnosis, and
  the control asserts the *reason* — a `ZodError` naming a JSON-RPC field — rather than any failure.
  `validate-real-tools.mjs` carries the same self-check: it re-injects the original defect into the real payload
  and fails if the meta-schema no longer rejects it.

`tests\ProtocolSmokeTest` contains one further control worth noting: this host does not report a dead loopback port
consistently — a started, stopped and never-connected `TcpListener` alternates between `ConnectionRefused` and a
connect that hangs until it times out. The probe therefore classifies refused / hung / accepted, asserts that the
stopped port is **not accepting**, and probes the *live* port first to confirm the probe can see a listener.

### Load path and constructibility

`tests\LoadabilityCheck` inspects the built plugin DLL and manifest for the failure modes that are invisible at
compile time but present in-game only as "plugin failed to load" with no useful detail: a malformed or incomplete
manifest, a `DalamudApiLevel` that does not match the installed Dalamud (which Dalamud refuses silently), a
`Plugin` constructor parameter whose type is not a Dalamud service type, and a duplicate tool name
(`ToolRegistry.Add` throws on a repeat, and registration happens inside the `Plugin` constructor). It loads the
plugin assembly directly and compares it against Dalamud's own type tables, linking no source, so it validates the
shipped binary rather than the sources.

The same suite settles the dependency-injection question using Dalamud's own container code rather than a copy of
it. `RegisterInterfaces` is pure attribute reflection and `ValidateCtor` never dereferences a service instance — it
reads only the *types* in `instances` plus `[ScopedService]` attributes — so the test builds a real
`ServiceContainer`, calls `RegisterInterfaces` for every concrete `IServiceType` exactly as
`InitializeEarlyLoadableServices` does, installs one singleton key per non-scoped service
(`Task.FromResult<T>(null)` is sufficient, since only the key's type is ever inspected), hands it a
`DalamudPluginInterface` stand-in as the scoped object, and invokes Dalamud's own private `FindApplicableCtor`. The
verdict is Dalamud's, not this project's reimplementation of the rule, and it confirms that all twenty-three
constructor parameters resolve. Four controls guard the result — a type the probe must refuse, a resolvable
constructor it must accept, a rebuild that withholds the singleton keys and asserts the same constructor is then
refused, and a check that the verdict depends on the services Dalamud registers.

**Offset audit.** The direct-struct tools (`get_job_gauge`, `get_active_statuses`) read game memory through
hand-copied field offsets from FFXIVClientStructs, and a library update that moves a field would not throw — it
would silently read another member's bytes. The test therefore re-derives every declared constant from the
installed `FFXIVClientStructs.dll` itself (attribute offsets rather than `Marshal.OffsetOf`, because these types
carry pointer members `Marshal` cannot lay out): `BattleChara.StatusManager` at +9136, `StatusManager`'s owner,
entry array, special-status timer, valid-status count (+984) and extra-flags byte (+985), its `StructSize` (992),
`GameObject.ObjectKind` (+144), and `JobGaugeManager`'s `ClassJobId` (+88) and gauge union (+8, with every union
member verified to share that one offset). It also cross-checks the `ClassJobId` → gauge-struct table: every
concrete gauge struct the library ships is covered, none is invented, and the covered job ids are exactly the 21
combat jobs known to have dedicated gauges. A FFXIVClientStructs update now fails here, by name, instead of
corrupting a read in game.

**Localization audit.** Both language tables in `Localization.cs` are checked for key parity — every key must
resolve in both, so a string added to English but forgotten in Chinese (or vice versa) fails the suite instead of
silently showing English to one language's users forever.

`tests\PluginLoadTest` goes further and runs the shipped `Plugin` constructor out of game. It loads `DalamudMCP.dll`
by path and instantiates `DalamudMCP.Plugin` with the twenty-three Dalamud services it asks for, each synthesized by
`DispatchProxy`, then speaks MCP to the listener over real TCP. That exercises the actual load path end to end:
config load and `Sanitize`, construction of the service graph, all eighteen tool sets registering into the real
registry, the ImGui window construction, the `UiBuilder.Draw` / `OpenConfigUi` subscriptions, the `/dalamudmcp`
command registration, the HTTP listener binding the configured port, handlers running through `GameThread` and
returning well-formed JSON, and a clean `Dispose` that unsubscribes both events, removes the command and stops the
listener. During that run every registered tool was swept with no arguments, and every handler either returned a
payload or answered with a deliberate, phrased error (not-logged-in, missing required parameter) rather than
escaping an exception.

Three config settings are asserted through the shipped plugin rather than against a hand-built server: the request
log (`LogRequests`), the bearer token (`AuthToken`) and the port. The first two are set on the config instance after
the plugin is constructed, which also proves the plugin reads them per request instead of snapshotting them at
startup — that is what makes toggling them in the settings window take effect without a plugin reload.

The `/dalamudmcp` chat command is run, not merely checked for registration. `CommandInfo.Handler` is a public
property, so the test pulls the real handler delegate back out and invokes it exactly as Dalamud would, which means
`status`, `stop`, `start`, an unknown subcommand, `port <n>` and an out-of-range `port` are all exercised against
the live listener and the live config. The consequences are asserted rather than the output: stopping must actually
stop serving, starting must serve MCP again, `port <n>` must move the listener at once (releasing the old port and
persisting the setting), and an out-of-range port must be refused **without** changing anything.

The memory tools are verified against real memory here too. `MemoryProbe` reads the current process via
`ReadProcessMemory(GetCurrentProcess(), ...)`, and the load test hosts the plugin in its own process, so it can pin
a known byte pattern and assert the tool returns exactly those bytes back. The safety guard is checked by reading
address `0x1` and requiring a phrased refusal. Every one of the 87 shipped tool schemas is validated from
`tools/list` over the real socket: each `inputSchema` must be a JSON object with an object `properties`, every
property must carry a `type` that is one of JSON Schema's seven legal names, every `array` must say what its `items`
are, and every name in `required` must actually be declared. The same section cross-checks schema against
behaviour: each handler's own "missing required parameter: X" message is captured during the sweep and compared
against the `required` list for the 8 handlers that demand exactly one parameter. Handlers that accept a *choice*
of arguments (`"provide itemId or name"`) are deliberately not cross-checked, since no single parameter is missing
in those cases.

That check immediately found a real defect: `read_pointer_chain` advertised its `offsets` parameter as
`{ "type": "array of integer" }`, which is not a legal JSON Schema type and carried no `items`, so a strict client
or a generated binding would reject the whole tool. The call-site spelling is kept (it reads better in C#) and
`Json.ApplyType` now expands it to `{"type":"array","items":{"type":"integer"}}` on the wire. A second defect sat
in the same parameter's description, which read `e.g. [0x10, 0x1C0]`; JSON has no hex literals, so the schema was
teaching a malformed request. The description now shows valid JSON and the type is the legal union
`{"type":"array","items":{"type":["integer","string"]}}`, because the handler genuinely accepts both forms
(`src\DalamudMCP\Tools\MemoryTools.cs:687` `ParseOffset` takes `0x`-prefixed strings, bare hex-digit strings and
decimals). The test suite calls `read_pointer_chain` with `["0x10"]` and asserts the hexdump *starts* at the
marker, with the pointer deliberately stored 16 bytes short of it, so a decoder that read `"0x10"` as decimal 10
would land 6 bytes early and be caught.

### Real game data

Twenty-two of the twenty-three services in `PluginLoadTest` are inert `DispatchProxy` stubs. The data manager is
not, because a stub cannot be made to work there: `DispatchProxy` does not copy a method's generic parameter
constraints into the override it generates, so an override of
`ExcelSheet<T> GetExcelSheet<T>() where T : struct, IExcelRow<T>` fails inside the generated method body with
`TypeLoadException: GenericArguments[0], 'T', on 'Lumina.Excel.ExcelSheet`1[T]' violates the constraint of type
parameter 'T'` before the handler is reached. With an inert proxy, every tool reaching sheet data would report a
`TypeLoadException` regardless of whether the plugin was correct.

`tests\PluginLoadTest\RealDataManager.cs` addresses this with `Reflection.Emit`, which does copy the constraints.
It emits a type implementing `Dalamud.Plugin.Services.IDataManager` and — when the local game install is found —
constructs it against that install's `sqpack` files through Lumina, which reads them directly with **no game
process running**. The sheet tools are therefore exercised against real game data, and the suite asserts that no
sheet-reading tool escapes an exception:

```
  [note] data manager is REAL, reading C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\sqpack
  [note] Item row 1 read through the emitted manager: 金币
```

The sqpack directory is located by, in order: the `DALAMUD_MCP_SQPACK` environment variable, four well-known
install paths, then the `Lumina is ready: <path>` line in `dalamud.log` (the most reliable signal, since it is
what the launcher actually resolved). If none is found the test says so and falls back to the inert proxy rather
than failing — the protocol checks still run, and the real-data checks are skipped.

### Live verification

In-game verification has been performed once, on this machine, with a live client. All 33 read-only tools that
existed at that point were called over the real endpoint (`http://127.0.0.1:18777/mcp`) with FFXIV running and a
character logged in, and every one returned real game data (or the correct "empty" answer for genuinely empty
state — solo party, no FATEs, no targets, idle gauge). Highlights: `get_local_player` returned the live character
(name, level 100, job, HP/MP, world, position); `get_game_objects` enumerated the player, a minion and training
dummies with distances; `get_job_gauge` decoded `BardGauge` live; `get_active_statuses` located a `StatusManager`
at exactly the audited +9136 offset for both the player and a dummy; `read_object_memory` at +144 returned the
expected `ObjectKind` bytes; every Excel tool answered from the client's own data (CN locale — 火之碎晶, 吟游诗人,
强化药); `read_memory` at the module base returned the `MZ` header; `scan_signature` found real matches; and
`get_module_info` reported the real client module.

That session also caught three defects the offline suites could not see, each fixed and re-verified in game by
rebuilding and letting Dalamud's dev-plugin auto-reload pick up the new DLL:

1. `get_job_gauge` threw `ArgumentException` inside `Enum.GetName`, because the JSON-side `JValue` box was passed
   where a real CLR box of the enum's underlying type is required. A separate `BoxClr` helper now builds the true
   boxed value for reflection, and `Box` keeps producing `JValue` for JSON.
2. `get_game_data_sheet_info` crashed with `Could not determine JSON object type for type
   System.Reflection.RuntimePropertyInfo` — the `textColumns` list serialized raw `PropertyInfo` objects. It now
   emits property **names**.
3. `get_game_data_row` always read row 0: the reflection buffer passed to `TryGetRow` never boxed the requested
   row id, and on .NET 10 `MethodInfo.Invoke` silently converts a `null` argument for a `uint` parameter into `0`
   instead of throwing. The id is now boxed explicitly.

## Limitations

Verified before that session and still standing: the plugin project builds clean for x64 against Dalamud API level
15 with the assemblies from a real install (`0 errors, 0 warnings`); the five suites pass with the results listed
in [Verification](#verification); and the plugin DLL and manifest are produced in the build output against the
correct Dalamud and FFXIVClientStructs assemblies.

Not verified, and therefore not claimed:

- In-game behaviour beyond the single verification session described above — one machine, one client build, one
  logged-in character in one zone. Other locales, other jobs' gauges beyond Bard, combat in progress, parties,
  dungeons and GPose were not exercised.
- That the offsets match a **future** game client build. They match the client that ran during verification.
- Long-session behaviour, such as memory growth in the session store or the listener surviving many hours of
  polling.

**Other limitations:**

- `tests\LoadabilityCheck` and `tests\PluginLoadTest` locate the repository by walking up from their own binary (or
  honor a `DALAMUD_MCP_REPO` environment variable) and read Dalamud assemblies from
  `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`, so they only run as-is on a CN-launcher machine. With the global
  launcher they exit non-zero.
- The request log records the method, tool name, outcome and elapsed time — deliberately not argument bodies or
  result payloads, so it will not tell you *what* was passed to a call. Raw memory reads are logged by name only.
- The plugin itself has no stdio transport; HTTP-capable clients connect directly and stdio-only clients go
  through `bridge\DalamudMcpBridge`.
- The mutating tools (control, chat, plugin management, addon windows, movement, teleport, screen capture, IPC
  endpoint registration) were exercised only out of game, where every handler answered with the expected
  deliberate error (not-logged-in or a refused parameter). Their in-game behaviour has not been verified, and the
  event collector, chat-log capture and IPC bridge have not run against a live client at all.
- The tools added after the verification session — `capture_game_screenshot`, `teleport_to_aetheryte`,
  `move_to_entity`, `move_to_nearby_targetable_object`, `get_chat_log`, `events_wait`, `slash_command`,
  `use_duty_action`, `get_quest_status`, `get_available_quests`, `plugin_data_subscribe`, `plugin_data_poll`,
  `plugin_data_unsubscribe`, `plugin_list`, `plugin_describe`, `get_addon_strings` and `select_addon_menu_item` —
  are covered by the offline suites (schema validation, the live-socket sweep and the loadability check) but have
  **not** been driven against a running game client. Their argument contract, error text and JSON shape are
  tested; their effects on the game are not.
- `move_to_entity` and `move_to_nearby_targetable_object` additionally depend on the third-party
  [vnavmesh](https://github.com/awgil/ffxiv_navmesh) plugin being installed and loaded; without it they return a
  phrased error rather than a partial path. That IPC handshake has not been exercised against a live vnavmesh
  build here.
- `capture_game_screenshot` returns the PNG base64-encoded inside the JSON-RPC result, so a full-resolution capture
  is a large response; `maxDimension` (default 1920) is the control for that, and `save=true` writes the file to
  disk instead for agent pipelines that can read a path.
- `get_game_objects` and similar enumeration tools read the whole object table and filter in memory, which is fine
  at normal object counts but not designed for tight polling loops.
- `latest.zip` is a plain plugin-repository package and is not signed or published anywhere; `.github\workflows\build.yml` only uploads it as a CI artifact.
- The session store keeps sessions in memory with a 2-hour idle cutoff, so a long-idle client must re-initialize.
