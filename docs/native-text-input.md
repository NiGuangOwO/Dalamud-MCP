# Native text input

`set_addon_text_input` replaces one native game's `TextInput` value using the installed SDK's `AtkComponentTextInput.SetText`. It is mutation-gated, including `dryRun`. It does not synthesize a key, submit chat, press Enter, activate a search button, or construct a callback payload. Native input listeners may still react to a text edit.

Discover the loaded addon and `componentType="TextInput"` through MCP. Copy `nodeId` and `componentPath`; omitted/empty path means top-level only. Run `dryRun=true` first. Optional `expectedText` and `expectedAddonId` guards refuse changed state. Node ids and addon ids must be rediscovered after replacement; the addon id alone is not a lifetime token.

Text must be a JSON string containing single-line plain Unicode, at most 256 UTF-8 bytes. Empty clears the field. NUL, control characters, Unicode line/paragraph separators, malformed surrogate sequences and oversized input are refused. Native length/sanitization rules remain in effect: inspect returned `text` and `exactMatch` rather than assuming the requested string was retained. Identical text sends no native edit.

The tool resolves the addon on the framework thread and checks exact name, readiness, visibility, component discriminator, owner node and owning addon. Scoped ULD arrays refuse duplicate ids. Hidden/cyclic/unreadable ancestry and disabled controls are refused. Before editing, the SDK function must resolve to executable memory. After editing, identities are re-resolved; replacement returns `verified=false` instead of reading a stale control. No hook, subscription or held input remains afterward.

`dryRun` returns the current text, so `exactMatch=false` can simply mean the proposed edit would change it. `verified=true` means the field was read back, not that a surrounding search or task completed.

## Exploration and verification on 2026-10-24

The complete running MCP catalog was read across both `tools/list` pages (100 + 19 tools), with mutation enabled. It had `imgui_text`, but no native text setter. The gap was encountered while trying to search the crafting log by name.

After adding the tool, the running build MVID matched the tested Debug assembly. `RecipeNote` accepted Chinese text `枫木`, the native text node displayed it, and clearing restored the empty string. This test does **not** establish recipe-search completion: the loaded Artisan integration labels the field `Artisan 搜索`, and submitting that search was not validated. No target-plugin source was consulted.

A second scenario completed a native search: open `MountNoteBook`, select its observed search tab, set `陆行鸟`, then click the independently observed search button (`ButtonClick` registered on that button). After the asynchronous update, the result list contained 专属陆行鸟、黑陆行鸟、大陆行鸟、红大陆行鸟、胖陆行鸟、游行陆行鸟. Repeating the same text dispatched no input; a wrong `expectedText` was refused. A field under an inactive hidden panel was refused before editing. Search text was cleared, the original tabs restored, and both test windows closed. No crafting, mount summon, spending or chat sending was performed by this exploration.

Debug and Release builds completed with zero warnings/errors. NativeUiReadTest passed 78/78 on both builds, PluginLoadTest passed 143/143 on Release, and ProtocolSmokeTest passed 50/50. The offline plugin sweep still reports its two existing inert-service exceptions; it is not certification of all game handlers. Coverage includes synthetic SDK TextInput ownership/discriminator/string-pointer checks, hidden/disabled/cyclic ancestry, plain-text/UTF-8 bounds, existing scoped-node regression coverage and the closed mutation gate. Run:

```powershell
dotnet build src/DalamudMCP/DalamudMCP.csproj -c Debug
dotnet build src/DalamudMCP/DalamudMCP.csproj -c Release
dotnet run --project tests/NativeUiReadTest/NativeUiReadTest.csproj
dotnet run --project tests/PluginLoadTest/PluginLoadTest.csproj
dotnet run --project tests/ProtocolSmokeTest/ProtocolSmokeTest.csproj
```

Other native fields, native formatting/truncation branches and Enter-only submission remain unverified. Use explicit observed UI actions for submission; do not infer it from a successful text edit.
