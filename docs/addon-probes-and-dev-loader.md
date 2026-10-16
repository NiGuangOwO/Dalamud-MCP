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

## Development plugin loader

`load_dev_plugin` takes an absolute `filePath` ending in `.dll`, with a sibling `.json` manifest, and optional `autoReload` (default true). It validates the manifest's API against the installed Dalamud, registers the developer location, asks PluginManager to scan, loads the resulting LocalDevPlugin and verifies `IsLoaded`. Existing developer plugins at the same path are reused. A conflicting installed internal name at a different path, non-dev instance, or the MCP host itself is refused.

This uses narrowly scoped, shape-checked reflection over Dalamud's managed developer-plugin APIs because no public load API is exposed. Missing API shapes produce tool errors. This is not an arbitrary reflection or code-execution endpoint. The APIs were checked against the installed assembly and then exercised against a real local development plugin. Development locations and auto-reload settings persist; play sessions do not automatically start.

`manage_plugin`'s `load` action uses the same verified loader. Other legacy actions still dispatch chat commands and must be followed by `plugin_list` or `plugin_describe`; their dispatch response is not proof of an actual lifecycle change.

## Callback-probe crash repair

The old `probe_callback_enable` decoded the return from `ISigScanner.TryScanText` as an E8 relative call site. The scanner already resolves leading E8/E9 signatures, so the extra decode produced an invalid address and crashed `HookManager.FollowJmp`.

The probe now takes `AtkUnitBase.MemberFunctionPointers.FireCallback` directly and refuses unresolved, unreadable, guarded or non-executable addresses before entering the hook engine. Offline PluginLoadTest verifies unresolved-pointer rejection; enabling, capturing and unloading the repaired probe were verified in the live client.

The catalog now contains 106 tools: 65 read-only and 41 mutating. MCP `tools/list` is paginated at 100 entries; follow `nextCursor` to retrieve the complete catalog.
