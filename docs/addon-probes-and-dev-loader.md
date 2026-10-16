# Addon frame probes and verified development-plugin loading

Added during Crystal Tower Striker development on 2026-10-16. All game observation and input used this MCP server. No computer-use tools were used.

## Read-only probes

`get_addon_node_snapshot` takes an exact `addon` name and 1–32 unique `nodeIds`. It reads local geometry, scales, visibility, enabled flags, registered event types/parameters/listeners, node addresses and animation timelines. Optional `componentId` scopes the IDs to a top-level component's UldManager, avoiding repeated child IDs.

`sample_addon_nodes` takes the same selection plus `frames` (1–600) and `intervalFrames` (1–60). Samples are taken on Framework.Update without blocking the game. Addon addresses are resolved again on every frame; an identity change ends the sample. Requests have a 20 second timeout, a single active sampler, bounded storage and cancellation on plugin unload.

```json
{"addon":"Hummer","nodeIds":[21,22,29],"frames":180}
```

## Frame-conditioned input

`click_addon_when_node_matches` takes `addon`, `observeNodeId`, `clickNodeId`, `minWidth`, `maxWidth`, optional `frames`, `event` and `param`. It observes each frame and dispatches at most one normal native UI event. The observed node must be visible, the clicked node visible and enabled, and the addon must retain its identity. The mutating-tools gate is checked again immediately before dispatch. It shares the sampler's bounds and unload cancellation. `reason=triggered` reports the dispatch; it does not claim that the game accepted or rewarded it. Callers must verify the resulting game state.

```json
{"addon":"Hummer","observeNodeId":21,"clickNodeId":29,"minWidth":435,"maxWidth":448,"event":"buttonPress","param":0,"frames":180}
```

`click_addon_element` now also accepts `componentId`. This is necessary when child IDs are duplicated across component instances. For example, `componentId=29,nodeId=4` resolves Hummer's button collision node instead of an unrelated top-level resource node 4.

## Screen-space alignment (Monster Toss development)

Snapshots and samples now include native `screenX`/`screenY`, origin, rotation, parent id and the 2x2 transform matrix. The first 32 integer/unsigned/float/bool AtkValues are captured in the same framework frame as the nodes; other value types are omitted and `valueCount` reports the full table length.

`click_addon_when_node_matches` optionally accepts `targetNodeId` instead of `minWidth`/`maxWidth`. It compares the observed node's screen-space right edge with the marker's transformed horizontal center. `targetFraction` selects the central fraction of the marker width (default 0.5), preserving behavior under UI zoom. Native ScreenX already incorporates the origin and ancestor transforms: summing local X values can give the wrong aim. Rotated, reflected, hidden, degenerate and non-finite geometry cannot trigger this mode.

The optional `guardValueIndex` with `guardValue` or `guardValues` restricts input to a scalar addon state, evaluated in that same frame. This avoids a separate state-read/input race. For BasketBall, first-shot phase 2 and subsequent-shot phase 3 are both playable:

```json
{"addon":"BasketBall","observeNodeId":14,"clickNodeId":10,"targetNodeId":18,"targetFraction":0.5,"guardValueIndex":0,"guardValues":[2,3],"event":"buttonPress","param":0,"frames":600}
```

The Monster Toss experiment observed local-coordinate aim 203 miss, while screen-aligned aim 223 produced the game's native success callback `[11,1,0]`. Each successful shot moves the target marker, so the geometry must be recomputed every frame. `tests/AddonGeometryTest` covers those observations, zoom invariance, target movement and invalid geometry.

## Development plugin loader

`load_dev_plugin` takes an absolute `filePath` ending in `.dll`, with a sibling `.json` manifest, and optional `autoReload` (default true). It validates the manifest's API against the installed Dalamud, registers the developer location, asks PluginManager to scan, loads the resulting LocalDevPlugin and verifies `IsLoaded`. Existing developer plugins at the same path are reused. A conflicting installed internal name at a different path, non-dev instance, or the MCP host itself is refused.

This uses narrowly scoped, shape-checked reflection over Dalamud's managed developer-plugin APIs because no public load API is exposed. Missing API shapes produce tool errors. This is not an arbitrary reflection or code-execution endpoint. The APIs were checked against the installed assembly and then exercised against a real local development plugin. Development locations and auto-reload settings persist; play sessions do not automatically start.

`manage_plugin`'s `load` action uses the same verified loader. Other legacy actions still dispatch chat commands and must be followed by `plugin_list` or `plugin_describe`; their dispatch response is not proof of an actual lifecycle change.

The loader also verifies build identity: it reads the DLL's PE module MVID without loading that file into MCP, waits for the installed plugin's loaded assembly to match it, and requires 750ms of stable loaded state. `buildMvid` and `buildVerified=true` are returned only after that check. Optional `buildWaitMs` bounds this wait (1000-30000ms, default 15000). If the DLL changes again, or auto-reload does not bring the expected build online, it returns an error. It does not force-reload an already loaded obsolete assembly when its watcher is disabled.

This addresses an observed development race: a slash command sent immediately after a build could reach the old plugin instance, which then unloaded and lost the just-started test session. Call `load_dev_plugin` after building and before test commands, even if `plugin_list` already says the plugin is loaded. `tests/BuildIdentityTest` checks the metadata reader against a real loaded test assembly, a renamed copy and incomplete PE output.

## Callback-probe crash repair

The old `probe_callback_enable` decoded the return from `ISigScanner.TryScanText` as an E8 relative call site. The scanner already resolves leading E8/E9 signatures, so the extra decode produced an invalid address and crashed `HookManager.FollowJmp`.

The probe now takes `AtkUnitBase.MemberFunctionPointers.FireCallback` directly and refuses unresolved, unreadable, guarded or non-executable addresses before entering the hook engine. Offline PluginLoadTest verifies unresolved-pointer rejection; enabling, capturing and unloading the repaired probe were verified in the live client.

The catalog now contains 106 tools: 65 read-only and 41 mutating. MCP `tools/list` is paginated at 100 entries; follow `nextCursor` to retrieve the complete catalog.
