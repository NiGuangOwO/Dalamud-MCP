# Tool audit, 2026-10-17

Auditing tools outside the earlier Gold Saucer minigame workflow. Game reads and interactions use the MCP endpoint only; no computer-use automation. Source editing, builds and Git publication use local development commands.

## Inventory and equipment identity

Live `get_equipment` returned 12 items, but 10 HQ items had null names. Dalamud's `GameInventoryItem.ItemId` includes quality encoding (e.g. 1049296); Excel expects `BaseItemId` (49296). Inventory and equipment now resolve names with the SDK's BaseItemId and return both IDs, preserving existing encoded itemId behavior. Equipment also supplies the promised item level, glamour id and nonzero materia count.

Live verification after reload: all 12 equipment names resolve, including the 10 HQ items, and all 140-or-fewer inventory slots are queried normally. Debug/Release build with zero warnings/errors; PluginLoadTest passes 103/103. No gear or inventory content is changed.

## Sparse native status slots and gauge coverage

`get_active_statuses` previously treated NumValidStatuses (30 on this live character even with no active status) as a dense occupied prefix and returned empty entries. It now scans all 60 declared slots, filters id 0, preserves native slot indices, reports activeCount/count/omitted separately, and retains numValidStatuses as an unmodified native header field. Non-BattleChara object kinds return available=false before reading an unrelated layout.

Live verification: empty array returns zero statuses; a Companion object is refused; normal self Adloquium produced the real statuses 4209 and 297 with names, durations and 64-bit source object IDs. A seven-check synthetic suite covers holes, slot 59, removal, duration/parameter/source preservation and incomplete data. BeastmasterGauge (ClassJob 43), present in the installed SDK's actual union, is now mapped: LoadabilityCheck improves from 109/110 to 110/110. The current Scholar gauge was re-read live; Beastmaster gameplay itself was not exercised.

## Native UI discovery and lifecycle

A real Currency enumeration crashed twice while this audit was in progress. The second minidump was analyzed offline with dotnet-dump: UiTools.NodeLabel at line 1282, when taking the address of ButtonTextNode->NodeText. The initial guarded-string patch alone was insufficient: a noncanonical text-node pointer faults before the called reader runs. The exact pointer pattern 0x7FFF00000003 reproduced an access violation in an isolated test process; complete node copying before field addressing fixes that negative control.

Root component discrimination was also wrong: AtkUldManager.ComponentData is AtkUldComponentDataBase navigation data, whereas Objects points at AtkUldComponentInfo for component ULDs. Confusing them could classify a window as a button and interpret unrelated bytes as ButtonTextNode. Metadata type checks and the test fixture now distinguish those fields. Labels, components and strings use complete guarded reads; enumeration uses bounded ULD arrays instead of untrusted child/sibling recursion, with a 4096-node traversal budget. list_addon_elements now exposes componentType. The shared MemoryProbe.TryReadStruct rejects partial page-end buffers before unmarshalling.

NativeUiReadTest passes 12/12 including the reproduced crash input, invalid nodes/buffers/label arrays, a readable label array containing a bad string pointer, valid text/list labels and partial native reads. Live Currency enumeration after verifying the running health buildMvid against the tested DLL reads all 482 nodes without a crash. Two client restarts were required during investigation; an offline pass alone was not represented as live success.

The unconditional delayed second Agent.Show was independently observed to close ContentsFinder at about 1273ms. open_addon now sends one Show, and callers wait for readiness. The duty list remains readable after 1800ms. Duty list tools refuse other addon layouts before native casts, and header detection uses the native SectionHeader discriminator instead of guessing from UIntValues[0]. Live wrong-layout read and mutation attempts against Inventory are refused; the actual 13-row list reports the two real category headers correctly. Duty registration/queue entry was not tested.

The read-only health endpoint includes buildMvid so future development tests can verify the exact MCP host build, including when the host itself cannot be passed to load_dev_plugin.
