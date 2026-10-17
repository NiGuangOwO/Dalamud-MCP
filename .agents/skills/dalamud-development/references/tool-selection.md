# Choose tools by the question

Use the connected schemas for exact arguments, limits and defaults. This is a routing guide, not a complete tool catalog. Server aliases and tool namespace prefixes may differ between clients.

| Question | Useful tools and interpretation |
| --- | --- |
| Is the client ready for this test? | `get_client_state`, `get_local_player`, `get_conditions`; read the returned loading/occupied state before interacting. |
| Which nearby object is relevant? | `get_game_objects`, `find_game_object`; resolve live object identity rather than reuse an address from a previous process, zone or object lifetime. |
| What does a row, status or action mean? | `search_game_data`, `get_game_data_row`, `get_item`, `get_action`, `get_status`; use the installed client's localized data. |
| Which native game window is present? | `list_loaded_addons`, then `get_addon_state` or `get_addon_strings`. Discovery returns exact addon names; it does not expand `open_addon`'s allowlist. Read each tool's supported inputs. |
| Which node changes, and when? | `list_addon_elements`, `get_addon_node_snapshot`, `sample_addon_nodes`; choose a small node selection. Child node IDs can repeat across components, so use `componentId` where supported. |
| Which event corresponds to normal interaction? | Use observed node events or a narrowly scoped `probe_receive_event_enable` / `probe_callback_enable`, inspect `probe_receive_event_dump` / `probe_callback_dump`, and remove with `probe_receive_event_disable` / `probe_callback_disable`. A listener hook has its own `probe_receive_event_enable_listener`; consult its schema and limit observation to the relevant addon. |
| How to perform a known UI interaction? | `click_addon_element`, `select_addon_menu_item`, or `click_addon_when_node_matches` as appropriate. Confirm visibility/enabled state, addon identity and current phase. Raw `fire_addon_callback` needs an established argument contract, not guessed indices. Verify the transition afterward. |
| Is an input tied to moving geometry? | `sample_addon_nodes`, then `click_addon_when_node_matches` with geometry/state guards in the same frame. Prefer native screen transforms to summing local coordinates. A one-event conditional click is not an atomic press/hold/release controller; handle timeout and cleanup without releasing into a replacement addon. |
| Is the new plugin build running? | `load_dev_plugin` checks the loaded DLL's MVID, not just its name; `plugin_list` / `plugin_describe` inspect lifecycle state. MCP cannot load itself through that tool. |
| Can an existing plugin expose the needed value? | `list_ipc_endpoints`, then `call_plugin_ipc` if the endpoint contract is suitable. Calls are mutating-gated even when their signature returns a value; read the provider's semantics. Integers and booleans must use native JSON types. |
| What changed without polling full state repeatedly? | `query_events`, `events_wait`; pass the returned `lastId` as the next `afterId`. `plugin_data_subscribe` / `plugin_data_poll` / `plugin_data_unsubscribe` cover supported plugin data subscriptions. Empty results or a dropped event history are not proof that no game transition occurred. |
| Is a typed API insufficient? | `get_module_info`, `query_memory_region`, `read_memory`, `read_pointer_chain`, `read_object_memory`, `scan_signature`; first read the native-debugging reference. |
| Is an actual picture needed? | `capture_game_screenshot` with `imageContent=true` returns a native MCP image; default `area=client` copies the game render target, not the desktop. Minimized capture can succeed while rendering continues. Use `includeDalamudUi=false` to exclude plugin overlays. Cached fallback requires `allowCached=true`; inspect `cached`, `ageMs` and `capturedAtUtc`. `save=true,includeImage=false` avoids transmitting pixels. |
| Must the game's window be minimized/restored? | `set_game_window_state` is a separate mutating-gated action, usable without login. Use only within the task's authorization; verify `observed`. Screenshots never restore/focus the window automatically. |

`imgui_*` targets Dear ImGui input/windows; it is not the native Atk addon tree. Use native addon tools for node identity and event contracts. Respect any user prohibition on visual input or computer use; having an input tool does not override it.

Tool responses describing a command/event dispatch are intermediate evidence. For example, follow a duty-list selection with a fresh list/state read; do not treat `fired=true` as proof of a selected duty or queue registration. Missing windows, an unavailable action and an idle cancellation are valid contextual outcomes, not successful gameplay tests.
