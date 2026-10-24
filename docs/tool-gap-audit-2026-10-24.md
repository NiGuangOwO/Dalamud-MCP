# Play-test gap audit, 2026-10-24

The server was exercised by actually playing the game through the MCP endpoint only — no computer-use
automation, no golden-ticket shortcuts. The goal was not coverage for its own sake but to find tasks
where the tool surface forced a workaround, guessed a value or gave up, and then to close the ones that
are cheap to close correctly. Six tool changes shipped with this audit; the gaps that were deliberately
left open are recorded at the end.

Character for the whole session: level 100 Machinist (“机工士”, ClassJob 31), gearset 15 of 36, in
中拉诺西亚 (territory 134, map 15), with a striking dummy (“木人”, gameObjectId 1073748164) as target.

## Position, raw coordinates and cancelling a walk

`get_local_player` was the only source of a position, and it is expensive: it collects statuses, gear and
account data on every call, which is the wrong price for “where am I” during movement. Nothing reported
heading in a human-readable form, nothing could walk to raw coordinates (only `move_to_entity` /
`move_to_nearby_targetable_object` through [vnavmesh](https://github.com/awgil/ffxiv_navmesh)), and once a
walk had started no tool could cancel it.

Added `get_position` (cheap: local player transform only — x/y/z, rotation, compass bearing, territory,
map, `inCombat`/`mounted`/`betweenAreas`, plus the distance to one object when asked), `move_to_position`
(snap to the floor mesh, then pathfind) and `stop_movement`.

Live verification: `get_position` reported 253.5 / 106.0 / -214.23, rotation 0.3788, compass 158.3°. The
compass convention was checked two independent ways rather than trusted — the bearing to the dummy
computed from world coordinates (Δx ≈ 0.06, Δz ≈ +0.15 → 158.2°) matched 158.3°, and after
`move_to_position` the character really walked (z −214.23 → −212.2, 2 yalms) and reported 179.9°, which
is the documented “rotation 0 faces south” convention.

`stop_movement` had a genuine unknown: the vnavmesh stop entry point is not part of this repository's
proven IPC set. It therefore probes a fixed ordered candidate list and invokes only what
`HasFunction`/`HasAction` proves exists. In this client `vnavmesh.Path.Stop` is registered as an action
gate and was invoked; no `IsRunning` probe exists here, so the response omits `pathRunningAfter` instead
of inventing it. `move_to_entity` and `move_to_nearby_targetable_object` keep their previous arguments,
payload keys and error text — the IPC binding was extracted into a shared `NavmeshBridge` rather than
duplicated.

## Switching job

The only way to change job was `/gearset change N` through the slash-command fallback, whose success was
never verified, and nothing could list which gearsets exist — so an agent could not name the job it
wanted. Added `get_gearsets` and `equip_gearset`.

The native surface had two traps worth recording. `GearsetEntry.NameString` is a getter that scans for a
NUL terminator with no bound, so the name is decoded from the bounded 48-byte `Name` span instead. And
`FindGearsetIdByName` is documented as a case-sensitive *prefix* search, so name resolution is done on the
managed side (exact first, then a unique substring) and ambiguity is refused with the candidate list.

Live verification: 36 gearsets, current index 15, job MCH. `equip_gearset {"name":"骑士"}` returned
`equipResultCode 0`, `alreadyActive false`, `waitedMs 159`, and before/after blocks showing index 15 →
0 and job 31 → 19 (PLD). The switch back by id (`{"gearsetId":15}`) restored MCH. This also settled an
offline unknown: `EquipGearset` returns 0 and `CurrentGearsetIndex` does advance on success.

## Actions that silently did nothing

Two live failures were worse than a missing tool, because they returned success:

- `execute_action` without `targetObjectId` used the self target id (`0xE0000000`) even with a monster
  hard-targeted, and answered `success: true` while the attack went nowhere.
- `toggle_sprint`, `use_general_action` and `dismount` returned a bare `{"success": false}`, so a cooldown
  was indistinguishable from range, a missing resource or a blocking condition.

`execute_action` now resolves the target as explicit id → current hard target → self, and reports
`targetName` and `targetSource`. A refused cast carries the game's `GetActionStatus` code plus a
plain-language cause; the general-action family reports the same. Existing keys (`actionId`,
`targetObjectId`, `success`) are unchanged.

Live verification: `execute_action {"actionName":"野火","dryRun":true}` reported `targetObjectId`
1073748164, `targetName` 木人, **`targetSource` “currentTarget”**, status 0. The real cast then returned
`success: true`, and `get_position` afterwards reported `inCombat: true` — the attack landed rather than
being dispatched. `{"actionName":"分裂弹","dryRun":true}` produced status 572 with the reason “the action's
use conditions are not met (combo, stance, or state)”, which is exactly the information that was missing.

## Which abilities are on cooldown

Nothing could answer this, so an agent had to guess whether an ability was usable. Added `get_cooldowns`:
every action of the current job whose runtime recast group is active and not yet elapsed, with remaining
and total seconds and current/maximum charges. The GCD group is excluded deliberately — a weaponskill
list would otherwise bury the answer.

Live verification: with nothing used, `count 0` plus the “nothing is on cooldown” note; immediately after
firing 野火, `{actionId 2878, name 野火, recastGroup 19, remainingSeconds 117.5, totalRecastSeconds 120.0,
currentCharges 0, maxCharges 1}`. Both branches are therefore exercised, and the 117.5 s of a 120 s
recast confirms the `RecastDetail` units are seconds.

## Gaps recorded but not implemented

These were hit during play and are left open on purpose: using an item, crafting and gathering, market
board and vendor purchase, quest accept and turn-in, retainer summoning, party/friend/FC actions,
inventory mutation (discard, move, sort) and equipping arbitrary gear rather than a whole gearset. They
are not thin wrappers — each needs its own state verification, cost/spend boundary and refusal design, and
several touch the mutation gate's spending restrictions. Wrapping them badly would repeat the
`execute_action` mistake this audit just fixed.

`get_active_statuses` returned an empty status list for a character that had no statuses, which is
consistent but not independent proof; the offline StatusSlotTest coverage and the earlier live Scholar
verification remain the evidence for that tool.

## Verification

Debug and Release both build with 0 warnings and 0 errors. `PluginLoadTest` passes 160/160 with the tool
count asserted at 137, and `ProtocolSmokeTest` passes 50/50. After Dalamud reloaded the dev plugin, the
running endpoint listed 137 tools, and all six new or changed tools were exercised against the live client
as described above.

No item was used, nothing was bought or spent, no equipment was changed. The only persistent character
change was the gearset switch, which was reverted to the original gearset 15 before the audit ended.
