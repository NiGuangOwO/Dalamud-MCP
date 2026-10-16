# Tool audit, 2026-10-17

Auditing tools outside the earlier Gold Saucer minigame workflow. Game reads and interactions use the MCP endpoint only; no computer-use automation. Source editing, builds and Git publication use local development commands.

## Inventory and equipment identity

Live `get_equipment` returned 12 items, but 10 HQ items had null names. Dalamud's `GameInventoryItem.ItemId` includes quality encoding (e.g. 1049296); Excel expects `BaseItemId` (49296). Inventory and equipment now resolve names with the SDK's BaseItemId and return both IDs, preserving existing encoded itemId behavior. Equipment also supplies the promised item level, glamour id and nonzero materia count.

Live verification after reload: all 12 equipment names resolve, including the 10 HQ items, and all 140-or-fewer inventory slots are queried normally. Debug/Release build with zero warnings/errors; PluginLoadTest passes 103/103. No gear or inventory content is changed.
