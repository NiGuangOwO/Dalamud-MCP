# Tool audit, 2026-10-17

Auditing tools outside the earlier Gold Saucer minigame workflow. Game reads and interactions use the MCP endpoint only; no computer-use automation. Source editing, builds and Git publication use local development commands.

## Inventory and equipment identity

Live `get_equipment` returned 12 items, but 10 HQ items had null names. Dalamud's `GameInventoryItem.ItemId` includes quality encoding (e.g. 1049296); Excel expects `BaseItemId` (49296). Inventory and equipment now resolve names with the SDK's BaseItemId and return both IDs, preserving existing encoded itemId behavior. Equipment also supplies the promised item level, glamour id and nonzero materia count.

Live verification after reload: all 12 equipment names resolve, including the 10 HQ items, and all 140-or-fewer inventory slots are queried normally. Debug/Release build with zero warnings/errors; PluginLoadTest passes 103/103. No gear or inventory content is changed.

## Sparse native status slots and gauge coverage

`get_active_statuses` previously treated NumValidStatuses (30 on this live character even with no active status) as a dense occupied prefix and returned empty entries. It now scans all 60 declared slots, filters id 0, preserves native slot indices, reports activeCount/count/omitted separately, and retains numValidStatuses as an unmodified native header field. Non-BattleChara object kinds return available=false before reading an unrelated layout.

Live verification: empty array returns zero statuses; a Companion object is refused; normal self Adloquium produced the real statuses 4209 and 297 with names, durations and 64-bit source object IDs. A seven-check synthetic suite covers holes, slot 59, removal, duration/parameter/source preservation and incomplete data. BeastmasterGauge (ClassJob 43), present in the installed SDK's actual union, is now mapped: LoadabilityCheck improves from 109/110 to 110/110. The current Scholar gauge was re-read live; Beastmaster gameplay itself was not exercised.
