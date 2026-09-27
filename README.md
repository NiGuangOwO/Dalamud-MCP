# Dalamud MCP

English | [简体中文](README.zh-CN.md)

A Dalamud plugin for FINAL FANTASY XIV that hosts a [Model Context Protocol](https://modelcontextprotocol.io)
server on a loopback port, so an AI agent can read live game state from a running client. The plugin runs
inside the game process, so it can read the same memory the client itself uses: the object table, the local
player, party and alliance, targets, FATEs, currency, Excel game data, and arbitrary validated raw memory
through [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs). An MCP client connects to
`http://127.0.0.1:18777/mcp` and calls 45 tools — 33 read-only plus 12 UI/diagnostic tools (opt-in, mutating) that can
open and close in-game addon windows. The 33 read-only tools were all verified against a live, logged-in
client — see [Limitations](#limitations).

## Build

The plugin targets `net10.0-windows7.0` and x64, and references the Dalamud assemblies from a local
XIVLauncher install rather than NuGet packages.

```powershell
cd <repo>\src\DalamudMCP
dotnet build DalamudMCP.csproj -p:Platform=x64
```

`DalamudMCP.csproj` resolves `$(DalamudLibPath)` automatically, preferring the CN install and falling back
to the global one:

- `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
- `%APPDATA%\XIVLauncher\addon\Hooks\dev`

Override it if your assemblies live elsewhere:

```powershell
dotnet build DalamudMCP.csproj -p:Platform=x64 -p:DalamudLibPath="D:\some\Hooks\dev\"
```

There is no `.sln`, and the plugin project itself has no NuGet package references — only `bridge\` and the test
projects use NuGet. `Dalamud.dll`, `Lumina`, `Lumina.Excel`,
`Newtonsoft.Json` and `Dalamud.Bindings.ImGui` are all `Private=false` references to the dev folder, so the
game's own copies are used at runtime. **FFXIVClientStructs comes via Dalamud** — `FFXIVClientStructs.dll` is
referenced from the same `Hooks\dev` directory that Dalamud itself ships, which keeps the structure offsets
in sync with the installed Dalamud version. The manifest declares `DalamudApiLevel: 15`.

Build output lands in `src\DalamudMCP\bin\x64\Debug\` and contains `DalamudMCP.dll`, `DalamudMCP.json`,
`DalamudMCP.deps.json` and `DalamudMCP.pdb`. The manifest is copied to the output directory automatically.

## Install as a dev plugin

The build output already contains everything Dalamud needs, so you can register it in place — no copy step
is required:

```
src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll
src\DalamudMCP\bin\x64\Debug\DalamudMCP.json
src\DalamudMCP\bin\x64\Debug\DalamudMCP.deps.json
src\DalamudMCP\bin\x64\Debug\DalamudMCP.pdb
```

### Activating in-game

1. Launch the game and use `/xlsettings` in chat or `xlsettings` in the Dalamud Console to open up the Dalamud settings.
    * In here, go to `Experimental`, and add the full path to `DalamudMCP.dll` from the build output above to the list of Dev Plugin Locations.
2. Next, use `/xlplugins` (chat) or `xlplugins` (console) to open up the Plugin Installer.
    * In here, go to `Dev Tools > Installed Dev Plugins`, and the `DalamudMCP` should be visible. Enable it.
3. Once loaded, the plugin starts the listener automatically (`Enabled` and `AutoStart` both default to true) and you can check it with `/dalamudmcp status`.

Note that you only need to add it to the Dev Plugin Locations once (Step 1); it is preserved afterwards. You can disable, enable, or load your plugin on startup through the Plugin Installer.

## Verify it works

`tests\ProtocolSmokeTest` exercises the real MCP server without the game. It **compiles the actual transport
sources** — `McpServer.cs`, `MiniHttp.cs`, `ToolRegistry.cs` and `Json.cs` are `<Compile Include>`d from
`src\DalamudMCP\Mcp` unmodified — so it is a genuine test of the running server, not a reimplementation.
Those four files deliberately have no Dalamud dependency, which is what makes the test possible.

```powershell
cd <repo>\tests\ProtocolSmokeTest
dotnet run --project ProtocolSmokeTest.csproj -p:Platform=x64
```

It starts the server on an ephemeral port, drives it over real loopback HTTP, and prints:

```
47/47 checks passed
```

The process exits non-zero if any check fails. It covers the `initialize` handshake and the negotiated
protocol version, `tools/list` with schemas and annotations, `tools/call` for success and both error paths
(`ToolException` and an unexpected exception), unknown-tool and unknown-method rejection, the mutating-tool
gate (refused while closed, revealed and allowed once opened), auth both on and off (bearer header and
`?token=` query), refusal of a `POST /mcp` with no session, 24 concurrent calls each
returning their own result, the opt-in request log (silent by default, one line per request when enabled,
naming the tool and reporting the outcome, and silent again when disabled), and clean start/stop/restart
behaviour.

Two of those checks are a control experiment, and it is worth knowing why. This host does **not** report a
dead loopback port consistently: a textbook `TcpListener` that is started, stopped, and never connected to
alternates between `ConnectionRefused` and a connect that hangs until it times out. So "the connect is
refused after stop" is not an achievable assertion — an earlier version of this test compared the server's
result against the control's and passed only by luck. The probe now distinguishes refused / hung / accepted,
asserts that the stopped port is **not accepting**, and probes the *live* port first to confirm the probe can
actually see a listener. That positive control is what makes the negative result meaningful.

This test proves the protocol and transport work. It cannot prove anything about game data, which requires a
running client.

`tests\BridgeSmokeTest` does the same for the stdio bridge. It hosts the real `McpServer` in-process, launches
the built `dalamud-mcp-bridge.exe` as a child process, and speaks newline-delimited JSON-RPC to its stdin:

```powershell
cd <repo>\tests\BridgeSmokeTest
dotnet run --project BridgeSmokeTest.csproj -p:Platform=x64
```

```
22/22 checks passed
```

That covers the handshake and session capture, notification suppression (a notification must produce no
reply), `tools/list`, argument and error round trips, survival of a malformed line, five pipelined requests
each getting their own id, and a clean exit on stdin EOF. It also runs the bridge twice more against a server
whose token gate is switched on: once **without** `--token` (the bridge must refuse to start and say the token
is missing) and once **with** it (the bridge's own handshake and a tool call must both get through). Build the
bridge before running it; the test looks for the newest `dalamud-mcp-bridge.exe` under
`bridge\DalamudMcpBridge\bin` and fails if it finds none.

`tests\LoadabilityCheck` inspects the **built plugin DLL and manifest** for the failure modes that are
invisible at compile time but present in-game only as "plugin failed to load" with no useful detail: a
malformed or incomplete manifest or a `DalamudApiLevel` that does not match the installed Dalamud (which
Dalamud refuses silently), a `Plugin` constructor parameter whose type is not a Dalamud service type, and a
duplicate tool name (`ToolRegistry.Add` throws on a repeat, and registration happens inside the `Plugin`
constructor).

```powershell
cd <repo>\tests\LoadabilityCheck
dotnet run --project LoadabilityCheck.csproj
```

```
103/103 checks passed
```

It loads the plugin assembly directly and compares it against Dalamud's own type tables in the CN dev
assemblies, linking no source at all, so it validates the shipped binary rather than the sources.

**It also answers the DI question, using Dalamud's own container code rather than a copy of it.** The earlier
version of this test could only report "the type exists" for each of the twenty constructor parameters,
because `ServiceContainer`'s interface map looks like it only exists in a running client. It does not:
`RegisterInterfaces` is pure attribute reflection, and `ValidateCtor` never dereferences a service instance —
it reads only the *types* in `instances` plus `[ScopedService]` attributes. So the test builds a real
`ServiceContainer`, calls `RegisterInterfaces` for every concrete `IServiceType` exactly as
`InitializeEarlyLoadableServices` does, installs one singleton key per non-scoped service
(`Task.FromResult<T>(null)` is enough — only the key's type is ever inspected), hands it a
`DalamudPluginInterface` stand-in as the scoped object, and then invokes Dalamud's own private
`FindApplicableCtor`. The verdict is Dalamud's, not this project's reimplementation of the rule:

```
112 service types mapped, 83 singletons installed, 29 scoped left out | 19 interface, 0 singleton, 1 scoped
[PASS] Dalamud's own container accepts the plugin constructor
[PASS] the container probe is capable of rejecting a constructor
[PASS] the container probe still accepts a resolvable constructor
[PASS] the verdict depends on the services Dalamud registers
[PASS] every parameter is reachable through Dalamud's own resolution paths
```

Three of those four are controls, and two of them exist because the first attempt at this probe was worthless.
The first control used `System.Version` as its unsatisfiable type; it passed for the wrong reason, because
`Version` has a parameterless constructor and `ValidateCtor` returns `true` for an empty parameter list without
consulting a single service — so the probe's "rejection" proved nothing. The replacement is a type defined in
the test with exactly one public constructor taking a `string`, and the check additionally asserts that the type
exposes exactly one constructor, since `FindApplicableCtor` returns `null` both when it refuses an unsatisfiable
constructor and when there is no constructor to consider. The fourth control is the causal one: it rebuilds the
container with the **same** interface map but withholds the singleton keys, and asserts the identical
constructor is then refused. Without that, "83 singletons installed" would be decoration and the verdict could
be an artifact of the rebuild rather than a statement about Dalamud.

**The offset audit.** The direct-struct tools (`get_job_gauge`, `get_status_effects`) read game memory through
hand-copied field offsets from FFXIVClientStructs. A library update that moves a field would not throw — it
would silently read another member's bytes. So the test re-derives every declared constant from the installed
`FFXIVClientStructs.dll` itself (attribute offsets rather than `Marshal.OffsetOf`, because these types carry
pointer members `Marshal` cannot lay out): `BattleChara.StatusManager` at +9136, `StatusManager`'s owner, entry
array, special-status timer, valid-status count (+984) and extra-flags byte (+985), its `StructSize` (992),
`GameObject.ObjectKind` (+144), and `JobGaugeManager`'s `ClassJobId` (+88) and gauge union (+8, with every
union member verified to share that one offset). It also cross-checks the `ClassJobId` → gauge-struct table:
every concrete gauge struct the library ships is covered, none is invented, and the covered job ids are exactly
the 21 combat jobs known to have dedicated gauges. A FFXIVClientStructs update now fails here, by name, instead
of corrupting a read in game.

**The localization audit.** Both language tables in `Localization.cs` are checked for key parity — every key
must resolve in both, so a string added to English but forgotten in Chinese (or vice versa) fails the suite
instead of silently showing English to one language's users forever.

Injecting a bogus parameter (`System.Net.Http.HttpClient`) makes the two real checks fail while leaving all four
controls passing — the discriminator is what tells a plugin defect apart from a broken probe. Build the plugin
first: the test looks for `src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll` at a fixed absolute path and exits with
code 2 if it is missing. What this still cannot prove: that the service instances themselves construct in game,
or that the game-side state they read is ready.

`tests\PluginLoadTest` goes further and **runs the shipped `Plugin` constructor out of game**. The three tests
above never execute the plugin type: two re-host the transport with a stub game thread and one only reads
metadata. This one loads `DalamudMCP.dll` by path and instantiates `DalamudMCP.Plugin` with the twenty Dalamud
services it asks for, each synthesized by `DispatchProxy`, then speaks MCP to the listener over real TCP.

```powershell
cd <repo>\tests\PluginLoadTest
dotnet run --project PluginLoadTest.csproj -p:Platform=x64
```

```
93/93 checks passed

The shipped plugin loads, registers its tools, binds its port, serves MCP, and unloads.
```

> The count depends on whether the machine's game data could be found: `93/93` with a real data
> manager, `87/87` without one. The extra six checks are the real-game-data assertions and the
> no-escaped-exception contract check.

That exercises the actual load path end to end: config load and `Sanitize`, construction of the service graph,
all five tool sets registering into the real registry, the ImGui window construction, the `UiBuilder.Draw` /
`OpenConfigUi` subscriptions, the `/dalamudmcp` command registration, the HTTP listener binding the configured
port, handlers running through `GameThread` and returning well-formed JSON, and a clean `Dispose` that
unsubscribes both events, removes the command, and stops the listener. It is the strongest out-of-game evidence
available for "the plugin will load" — and the load itself was later confirmed in game (see
[Limitations](#limitations)).

Three of the config settings are asserted **through the shipped plugin**, not against a hand-built server: the
request log (`LogRequests`), the bearer token (`AuthToken`), and the port. The first two are set on the config
instance after the plugin is constructed, which also proves the plugin reads them per request instead of
snapshotting them at startup — that is what makes toggling them in the settings window take effect without a
plugin reload. Before this, `Config.AuthToken` was only ever verified as a *field*; nothing checked that the
plugin forwarded it into the server's `TokenProvider`, so a wiring mistake would have left the port open while
the UI claimed it was protected.

The `/dalamudmcp` chat command is **run**, not merely checked for registration. `CommandInfo.Handler` is a
public property, so the test pulls the real handler delegate back out and invokes it exactly as Dalamud would —
which means `status`, `stop`, `start`, an unknown subcommand, `port <n>` and an out-of-range `port` are all
exercised against the live listener and the live config. The consequences are asserted, not just the output:
stopping must actually stop serving, starting must serve MCP again, `port <n>` must move the listener *at once*
(releasing the old port and persisting the setting), and an out-of-range port must be refused **without**
changing anything — a single typo must not silently move the listener somewhere unusable. Registering a handler
says nothing about whether its body works, and this command is the only control surface a user has for
start/stop/repoint without hand-editing a config file.

The memory tools are verified against **real memory** here too. `MemoryProbe` reads the current process via
`ReadProcessMemory(GetCurrentProcess(), ...)`, and the load test hosts the plugin in its own process, so it can
pin a known byte pattern and assert the tool returns exactly those bytes back. The safety guard — which exists
because a bad pointer would otherwise raise an access violation that kills the game client — is checked by
reading address `0x1` and requiring a phrased refusal; that the assertion can be written at all is evidence the
guard held.

Every one of the 36 shipped tool schemas is validated from `tools/list` over the real socket: each
`inputSchema` must be a JSON object with an object `properties`, every property must carry a `type` that is one of
JSON Schema's seven legal names, every `array` must say what its `items` are, and every name in `required` must
actually be declared. That check was added because it immediately found a real defect: `read_pointer_chain`
advertised its `offsets` parameter as

```json
{ "type": "array of integer" }
```

which is not a legal JSON Schema type, and carried no `items` — a strict client or a generated binding rejects the
whole tool. The call-site spelling is kept (it reads better in C#) and `Json.ApplyType` now expands it to
`{"type":"array","items":{"type":"integer"}}` on the wire. The check was verified by temporarily reverting the fix
and confirming it fails with `read_pointer_chain.offsets: type "array of integer" is not a JSON Schema type`.

A second defect sat in the same parameter's *description*, which read `e.g. [0x10, 0x1C0]`. JSON has no hex
literals, so an agent copying that example emits a malformed request — the schema was teaching the wrong input.
The description now shows valid JSON, and the type is the legal union
`{"type":"array","items":{"type":["integer","string"]}}` because the handler genuinely accepts both forms
(`src\DalamudMCP\Tools\MemoryTools.cs:687` `ParseOffset` takes `0x`-prefixed strings, bare hex-digit strings, and
decimals). Advertising the union is not decorative: the test suite calls `read_pointer_chain` with `["0x10"]` and
asserts the hexdump *starts* at the marker, with the pointer deliberately stored 16 bytes short of it, so a decoder
that read `"0x10"` as decimal 10 would land 6 bytes early and be caught. That is the difference between the schema
claiming to accept hex and the handler actually doing it.

The same section cross-checks schema against behaviour: each handler's own "missing required parameter: X" message
is captured during the sweep and compared against the `required` list, so a parameter the schema calls optional
that the handler actually demands is a failure. The comparison covers 8 handler demands. Handlers that accept a
*choice* of arguments (`"provide itemId or name"`) are deliberately not cross-checked — no single parameter is
missing in those cases, and guessing which one to check would invent a failure.

### An independent client judges the framing

Every suite above speaks JSON-RPC that the author of this project wrote by hand. That is a real blind spot: if a
framing detail were misread — a session header in the wrong place, an `initialize` result shape only this server
emits, a tool result an agent's SDK cannot parse — the hand-written tests would agree with the server's own mistake
and still pass. `tests\McpInterop` removes it by driving the real server with the **official** MCP SDK
(`@modelcontextprotocol/sdk`, the TypeScript implementation, pinned at `1.30.1`) and letting it judge the wire.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\McpInterop\run-interop.ps1
```

```
12/12 interop checks passed
2/2 negative-control checks passed
93/93 checks passed
11/11 real-schema checks passed
INTEROP OK
```

The host (`tests\McpInterop\host`) links the four `src\DalamudMCP\Mcp\*.cs` files, which have no Dalamud references
at all — that seam is what makes out-of-process protocol testing possible in the first place. The official client
completes `initialize`, reads back the server's identity, parses `tools/list`, round-trips a tool call, receives a
tool error as `isError` rather than a transport failure, and keeps working afterwards. It is also given the
array-of-union schema (the shape that had the real defect) to parse, since that is exactly the kind of detail a
client can choke on.

The suite's fourth phase validates all 36 **shipped** schemas, not synthetic ones: `PluginLoadTest` dumps its
`tools/list` payload when `DALAMUD_MCP_DUMP_TOOLS` is set, and that payload is run through the SDK's declared
`ToolSchema` and then compiled against the JSON Schema 2020-12 meta-schema using the `ajv` bundled inside the SDK
(36 schemas — exact parameter totals below reflect the last full interop run and are re-measured on schema changes).

Two things were learned by requiring the checks to be able to fail:

**The official SDK's own tool-schema validation is too lenient to catch this project's defect.** Its `ToolSchema`
types `inputSchema` as an object with a `type` of `"object"` and a `properties` record whose values are only
checked to be *objects* — never that a property's `type` is one of JSON Schema's legal names. Re-injecting the
original `{"type":"array of integer"}` defect into the real 36-tool payload still passed it. The label was
therefore rewritten to say only what it proves (`the official SDK accepts all 36 shipped tool definitions as
tools/list output`), and the ajv meta-schema pass was added, which rejects it with an independent message:
`type must be JSONType or JSONType[]: array of integer`.

**A negative control that passes for the wrong reason is not a control.** `negative.mjs` points the official
client at a deliberately non-conforming server and requires it to fail. It was, on one run, "passing" because the
client could not reach the server at all: Windows hands out ephemeral ports from 1024–15000, which overlaps the
WHATWG list of ports Node's `fetch` refuses *before connecting* (`1719` is one), so the host had bound a port no
Node client would talk to. Measured directly: with a listener bound on `1719`, `fetch` still fails with
`cause: bad port`. A `fetch failed` was being scored as a successful protocol rejection. Three fixes went in:
the host now re-binds until it lands outside the blocked list (`host\Program.cs`), `interop.mjs` refuses such a
port with a diagnosis instead of emitting a bare `fetch failed`, and — most importantly — the control now asserts
the *reason*: it requires a `ZodError` naming a JSON-RPC field (`invalid_union` at path `["jsonrpc"]`), and fails
if the rejection came from a transport failure. Verified by forcing the fake server onto `1719`, where the control
now reports `0/2` with

```
[FAIL] the official SDK REJECTS a non-conforming server -> rejected for the wrong reason
       (TypeError: fetch failed) - the client may never have reached the server, which would make
       this control vacuous
```

`validate-real-tools.mjs` carries its own self-proving control for the same reason: it re-injects the original
defect into the same real payload and requires the meta-schema to reject it, failing with
`the meta-schema accepted an illegal type name - the positive check is vacuous` if it ever stops doing so. A test
that cannot fail is not evidence.

### The data manager is real, not a stub

Nineteen of the twenty services are inert `DispatchProxy` stubs. The data manager is not, because a stub cannot
be made to work there.

`DispatchProxy` does not copy a method's generic parameter constraints into the override it generates, so an
override of

```csharp
ExcelSheet<T> GetExcelSheet<T>() where T : struct, IExcelRow<T>
```

dies **inside the generated method body** with

```
TypeLoadException: GenericArguments[0], 'T', on 'Lumina.Excel.ExcelSheet`1[T]' violates the constraint of type parameter 'T'
```

before the handler is ever reached. A seven-control standalone probe isolated the rule exactly: an F-bounded
constraint alone is fine (returned as itself, or used as an argument), a constrained generic return class is
fine under a plain `struct` constraint, and only the **combination** — an F-bounded type parameter flowing into
a class constrained the same way — fails. The real signature is that combination. So with an inert proxy, every
tool reaching sheet data reported a `TypeLoadException` regardless of whether the plugin was correct, and the
test was lying about why they failed.

`tests\PluginLoadTest\RealDataManager.cs` fixes that with `Reflection.Emit`, which *does* copy the constraints.
It emits a type implementing `Dalamud.Plugin.Services.IDataManager` and — when the local game install is found
— constructs it against that install's `sqpack` files through Lumina, which reads them directly with **no game
process running**. The result is that the sheet tools are exercised against real game data:

```
  [note] data manager is REAL, reading C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\sqpack
  [note] Item row 1 read through the emitted manager: 金币
  [PASS] get_item(1) returned the real CN item name
  [PASS] list_game_data_sheets reports the game's real sheet count
  [PASS] no sheet-reading tool escapes an exception against real game data
```

That last check is the one the inert proxy made impossible to write. It asserts that no sheet-reading tool
reports an escaped exception — a real defect signal rather than an artefact of the test.

The sqpack directory is located by, in order: the `DALAMUD_MCP_SQPACK` environment variable, four well-known
install paths, then the `Lumina is ready: <path>` line in `dalamud.log` (the most reliable signal, since it is
what the launcher actually resolved). If none is found the test says so and falls back to the inert proxy rather
than failing — the protocol checks still run, and the real-data checks and the no-escaped-exception check are
skipped.

What it deliberately does **not** claim, and reports honestly instead:

- **Not** that the twenty service *instances* construct in game, or that the game-side state they read is
  ready. `LoadabilityCheck` now does settle the separate question of whether Dalamud's container will hand the
  constructor those services at all, by rebuilding the real `ServiceContainer` offline and calling Dalamud's own
  `FindApplicableCtor`; what remains unverified here is the runtime behaviour of the services themselves.
- **Not** that every handler returns meaningful game data. Nineteen services are still inert, so handlers that
  touch the object table, memory, the sig scanner or game state see neutral values and report phrased errors
  (the sweep output shows exactly which, and how many). The sheet layer is the exception, and is real.
- **Not** anything about hooking a live client process. That gap has since been closed separately — see the
  in-game verification note in [Limitations](#limitations) — but this test itself still only ever runs out of
  game, and its honest scope is exactly what is written here.

Build the plugin first; like `LoadabilityCheck`, it locates the repository by walking up from its own binary
looking for `src\DalamudMCP`, and reads Dalamud from `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`.

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

Port and timeout changes need a listener restart; the window says so and offers a "Restart listener" button.
Other edits take effect immediately. Settings persist when a control loses focus after an edit, so dragging a
slider does not rewrite the config file every frame. The window also offers "Save now", "Start listener" /
"Stop listener" / "Restart listener", and "Open config folder".

The plugin ships English and Simplified Chinese strings (`src\DalamudMCP\Localization.cs`). With the default
`Language: auto`, a Chinese game client shows Chinese labels, command feedback, and the bind-failure toast;
an English client shows English. The tables compile into the plugin DLL (no satellite resources), missing
keys fall back to English, and `tests\LoadabilityCheck` fails loudly if a key exists in one table but not
the other.

`AllowMutatingTools` defaults to **off** because an agent should be granted write access explicitly rather
than receiving it as a side effect of installing a plugin. The gate is enforced in two places: mutating tools
are filtered out of `tools/list`, and `tools/call` rejects them with an explanatory error even if the client
knows the name. Turning the setting on also changes the label from "read-only (recommended)" to a warning
that "agents may change game state".

**Note:** the gate is fully implemented and tested, and the first mutating tools now ship behind it: the three
UI tools (`open_addon`, `close_addon`, plus the read-only `get_addon_state`) can open and close in-game addon
windows such as the inventory, armoury, or duty finder. `open_addon` and `close_addon` are flagged mutating, so
they stay invisible in `tools/list` and reject `tools/call` until `AllowMutatingTools` is turned on; the rest of
the tool set (33 tools) remains read-only, and there is still no chat-command or input-injection tool.

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

**`Mcp-Session-Id` is required after `initialize`.** Only the `initialize` request may be sent without it.
Every later POST to `/mcp` must carry the `Mcp-Session-Id` header returned by the handshake, or the server
answers `400` with `{"error":"session_required"}`. An id the server no longer knows returns `404` with
`{"error":"session_not_found"}` so the client re-initializes instead of looping. A conforming MCP client
handles this automatically.

If you set an auth token, every request must carry it — including `/health` and `/tools`. Only `OPTIONS`
(CORS preflight) and `GET /` are answered without it:

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

Some clients launch a subprocess and speak JSON-RPC over stdin/stdout instead of HTTP. This repository ships
a bridge for exactly that case: `bridge\DalamudMcpBridge`. It is a small, dependency-free console app that
reads newline-delimited JSON-RPC on stdin and forwards each message to the plugin's HTTP endpoint, so point
the client's `command` at the **bridge**, never at the plugin DLL.

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
`DALAMUD_MCP_PORT`, `DALAMUD_MCP_URL` and `DALAMUD_MCP_TOKEN` from the environment, so the flags can stay out
of the client config. It resolves to `http://127.0.0.1:18777` when nothing is given.

Two things it handles that a raw pipe would not:

- **Session capture.** The plugin issues a session id in the `Mcp-Session-Id` *response header* during the
  handshake. A stdio client only ever sees the JSON body, so it could never observe or echo that id. The
  bridge captures it and attaches it to every later request.
- **Re-handshake.** If the game or plugin reloads, the session id goes stale. The bridge detects the
  `session_required` rejection, mints a fresh session with its own `initialize`, replays the handshake
  notification, and retries the original request once — so a reload does not require restarting the client.

It also **fails fast and loudly on an auth mismatch**. Before it opens the stream it probes `GET /health`, and
a non-success answer is fatal rather than something to carry on past. A `401` prints whether the problem is a
missing token or a wrong one, and names the fix:

```
[bridge] the plugin requires a bearer token and none was supplied.
[bridge] pass --token <token> (or set DALAMUD_MCP_TOKEN) to match the plugin's AuthToken setting.
```

That distinction is checked by the test suite, because the failure mode it replaces was silent: `HttpClient`
does not throw on `4xx`, so a bridge without the correct token used to log `plugin health: 401`, start
"successfully", and then fail every single request afterwards with no explanation.

stdout carries protocol messages only; all diagnostics go to stderr, so `--verbose` is safe to leave on.

Verify the server is up before wiring a client:

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
Invoke-RestMethod http://127.0.0.1:18777/tools | ConvertTo-Json -Depth 4
```

## Tool reference

45 tools: 33 read-only plus 12 UI/diagnostic tools (`open_addon`, `close_addon`, `click_addon_element`, and the six `probe_*` tools that install hooks are mutating and hidden until
`AllowMutatingTools` is on; `get_addon_state` is read-only). Names are exactly as they appear in `tools/list`.

### Client and session

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_client_state` | Login status, zone/territory (id and name), map, instance, language, PvP/PvP-excluding-Den/GPose flags, idle state, the blocking condition flag, whether the local player object is loaded, framework update delta, last update time, process id and pointer size. The recommended first call, since most other tools report nothing useful while logged out. | none |
| `get_local_player` | The character you control: identity, world, job, level, HP/MP, position, statuses, plus account-level identity from `PlayerState` (content id, home/current world, race, tribe, sex, effective level, level-sync and grand company). Returns `loggedIn=false` when logged out, or `loggedIn=true` with `playerLoaded=false` while the character is still loading. | none |
| `get_player_attributes` | Character-sheet attributes via `IPlayerState.GetAttribute`, plus base Strength/Dexterity/Vitality/Intelligence/Mind/Piety. Non-zero attributes only. | none |
| `get_job_levels` | Level, experience and unlocked flag for each class and job, marking which is current. Jobs that are neither unlocked nor levelled are skipped. | none |
| `get_conditions` | Every `ConditionFlag` currently true, plus explicit booleans for `inCombat`, `mounted`, `crafting`, `gathering`, `betweenAreas`, `watchingCutscene`, `occupiedInEvent` and `boundByDuty`. | none |
| `get_currency` | Gil, FC gil, seals, allied seals, wolf marks, gold saucer coins, retainer gil, weekly tomestones, plus named currency-container items (tomestones, scrips, clusters). | none |
| `get_titles_and_achievements` | Pass `titleId` or `achievementId` to inspect one entry (name, description, points/completion for achievements; name, prefix flag, unlocked for titles). With neither, returns only sheet counts, whether the achievement/title lists are loaded, and a hint. | `titleId`, `achievementId` |
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

Tools that need a loaded character return `{"available": false, "reason": "not logged in"}` rather than
failing when you are at the title screen, so a call is always safe.

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
| `get_status_effects` | The raw 60-slot `StatusManager` of a BattleChara, read at its struct offset rather than through Dalamud's `StatusList`: owner address, the extra-flags byte, the special-status timer/direction float, and every status entry (id, param, remaining time, source object id, sheet name/description/stacks). Resolve the target like `read_object_memory`, or pass an absolute `address`. | `address`, `entityId`, `objectIndex`, `localPlayer`, `max` (default 60) |

Both tools degrade the same way everywhere else in this plugin: if FFXIVClientStructs' static address resolver has
not initialized (the plugin loaded before the game was ready) the tools return `{"available": false, ...}` with a
reason, never a thrown exception.

### UI (mutating, opt-in)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `open_addon` | Opens an in-game addon/system window through `AgentInterface.Show()` on the agent that owns it — the same path the game's own UI uses. The addon must be named from a fixed allowlist of ~66 agents (inventory, armoury, emote list, quest journal, achievements, mount/minion notebooks, orchestrion, teleport, duty finder, gear sets, config, currency, retainer, free company, and more); unknown or non-openable names are rejected rather than guessed. A freshly created window ignores synthetic clicks until the game refreshes it, so `open_addon` also schedules a second `Show()` ~1.2 s later (live-verified fix). | `addon` (required, enum of allowlisted names) |
| `close_addon` | Closes one of the same allowlisted addon windows through `AgentInterface.Hide()`. Closing a window that is not open is a no-op, not an error. | `addon` (required, enum) |
| `get_addon_state` | Read-only census: which of the allowlisted agents are currently active. Reports `active` and `inactive` name lists. | none |
| `list_addon_elements` | Walks a loaded addon's node tree (DFS from its root node) and reports each node's id, type, label, size, screen position, and whether it is clickable — the discovery half of UI automation. Component nodes (buttons, lists, drop-downs) report their runtime composite type. | `addon` or `addonId`, `maxDepth` (default 12), `maxNodes` (default 100) |
| `click_addon_element` | Dispatches one Atk UI event to a node inside a loaded addon through the addon's own `ReceiveEvent` — the same dispatch the real input pipeline feeds. Event types: `click` (MouseClick), `doubleClick`, `buttonClick` (ButtonClick 25), `buttonPress` (23), `buttonRelease` (24), or `registered` (fires every handler the node has registered). A real click is a press+release pair, so closing a window is `buttonPress` followed by `buttonRelease`. | `addon` or `addonId`, `nodeId` or `index`, `event`, `param` (advanced override) |
| `probe_receive_event_enable` | Diagnostic: swaps one loaded addon's `ReceiveEvent` vtable slot so every UI event it receives — real or synthetic — is captured before being forwarded. Use with `probe_receive_event_dump` to compare what a real click delivers versus what `click_addon_element` sends. | `addon` or `addonId` |
| `probe_receive_event_enable_listener` | Same capture, but hooks a specific node's first registered listener instead of the addon itself (real component clicks land on node-registered listeners). | `addon` or `addonId`, `nodeId` (required) |
| `probe_receive_event_disable` | Restores the hooked vtable slot. | none |
| `probe_receive_event_dump` | Returns every captured `ReceiveEvent` call: event type, param, and the raw event/event-data buffers decoded at their FCS offsets. Read-only. | none |
| `probe_callback_enable` | Diagnostic: hooks `AtkUnitBase.FireCallback` globally (the game's own addon callback entry, resolved from its call-site signature) so every callback the game performs is recorded with its decoded `AtkValue[]` arguments — the semantic layer real UI clicks funnel into, mirroring SimpleTweaks' Addon Logging → Callbacks. Use with `probe_callback_dump` to learn which `(addon, values)` payload reproduces an action. Mutating. | none |
| `probe_callback_disable` | Unhooks the FireCallback probe. Mutating. | none |
| `probe_callback_dump` | Returns every captured callback: addon name, value count, each `AtkValue` decoded by type (Int/UInt/Bool/Float/String/...), the close/update-visibility flag, and the return value. Read-only. | none |

These are the plugin's first mutating tools: `open_addon` and `close_addon` are registered with the mutating
flag, so they are filtered out of `tools/list` and rejected on `tools/call` while `AllowMutatingTools` is off
(the default). Failures (agent module not initialized, agent not openable) return structured
`{"available": false, "reason": ...}` instead of throwing, like the rest of the tool set.

Both directions were verified in-game with a live client: `open_addon currency` opens the Currency window and
`close_addon currency` closes it (confirmed visually). One quirk to know: `get_addon_state` reports
`AgentInterface.IsAgentActive`, which does not flip for every agent when its window shows — a window can be
visibly open while its agent still reports inactive. The census is a hint, not ground truth; `open_addon`/
`close_addon` are the reliable operations.

Click dispatch was likewise verified live: a synthetic `buttonPress`+`buttonRelease` pair on the Currency
window's close collision node closes the window, and the probe tools confirmed that real mouse clicks reach a
different receiver than the addon's vtable (component-registered listeners), which is why the `registered`
event mode and the listener-level probe exist.

Valid `format` values for `read_memory` and `read_object_memory`: `hexdump`, `bytes`, `u8`, `u16`, `u32`,
`u64`, `i8`, `i16`, `i32`, `i64`, `f32`, `f64`, `bool`, `string`, `utf16`, `pointer`.

Addresses are emitted and accepted as hex strings such as `"0x7FF6A1B2C3D4"`, because JSON numbers lose
integer precision past 2^53 and 64-bit pointers routinely exceed that. The parser also accepts a trailing
`h`, underscores, and bare decimal, but passing a quoted `0x` string is the reliable form.

## Safety model

**Read-only by default.** 33 of the 45 tools read state; none writes it. The mutating gate is enforced, and the
only tools registered behind it are the two UI tools that open and close addon windows — they stay invisible
until `AllowMutatingTools` is turned on. There is still no chat-command or input-injection tool.

**Loopback only.** The listener binds `IPAddress.Loopback` (`127.0.0.1`) directly with a `TcpListener`, not
`HttpListener`. That avoids the HTTP.SYS URL-ACL requirement — no elevation and no `netsh` reservation is
needed — and means the server is never reachable from the network. Do not expose the port through a proxy or
port-forward; the server has no TLS and no protection against that.

**The auth token is the only access control.** Within a loopback binding, any local process can reach the
port, so if the token is empty you are trusting every program on the machine. Set one if that matters to you.
The gate covers every endpoint that returns data or acts on the game. The only exceptions are `OPTIONS`
(CORS preflight) and `GET /` (a fixed transport advertisement naming the URLs, with no game data and nothing
that varies with your configuration), which are answered without it by design — see the endpoints section
above. Everything else, including `/health` and `/tools`, requires the bearer header when a token is set.

**Raw memory reads cannot crash the client.** This is the most important guarantee, and it is why
`MemoryProbe` exists: an agent can ask for an arbitrary address, and a bad pointer dereference inside the
game process would be an access violation that takes the whole client down. Before any read, `MemoryProbe`
calls `VirtualQuery` and refuses unless the page is committed and readable:

```csharp
var readable = mbi.State == MemCommit && (protect & PageNoAccess) == 0 && (protect & PageGuard) == 0;
```

Reads are then clamped to the end of the region, so a request that runs off the edge returns the readable
part instead of faulting, and the copy itself goes through `ReadProcessMemory` against
`GetCurrentProcess()`. Unreadable addresses produce a normal tool error describing the problem.

**Every tool call runs on the framework thread.** Game memory is only stable while the client's update loop
is running, so `GameThread` marshals each handler onto it via `IFramework.RunOnFrameworkThread`. A bounded
timeout prevents a wedged or zoning client from hanging an HTTP request forever; on timeout the call fails
with a message naming the reason ("The client may be loading, zoned, or busy") rather than blocking.

## Architecture

The path of one tool call:

```
MCP client
  -> POST http://127.0.0.1:18777/mcp        (JSON-RPC 2.0 over HTTP)
  -> MiniHttp         parses HTTP/1.1 by hand on a TcpListener; no HTTP.SYS, no ASP.NET
  -> McpServer        routes, enforces auth and session, dispatches JSON-RPC methods
  -> GameThread       IFramework.RunOnFrameworkThread + timeout
  -> tool handler     one of the five Tool* classes, called with the JSON arguments
  -> GameServices     Dalamud services (IObjectTable, IPartyList, IPlayerState, IDataManager, ...)
     + FFXIVClientStructs   direct reads of the client's own structures
  -> JSON result       serialized back as MCP text content
```

`MiniHttp` and `McpServer` have no Dalamud dependency, which is what lets the smoke test compile and drive
them outside the game. Everything above `GameThread` is transport and protocol; everything below it needs a
live client.

Registration is explicit rather than reflection-based. `Plugin` builds the object graph and calls
`ClientTools.Register`, `ObjectTools.Register`, `DataTools.Register`, `MemoryTools.Register` and `StructTools.Register` against a
shared `ToolRegistry`, whose `AllowMutating` delegate reads `Configuration.AllowMutatingTools` so the gate is
evaluated per call rather than cached at startup.

### Commands

| Command | Effect |
| --- | --- |
| `/dalamudmcp` | Open the settings window. |
| `/dalamudmcp start` / `stop` / `restart` | Control the listener. |
| `/dalamudmcp status` | Report running state and the endpoint. |
| `/dalamudmcp port <1024-65535>` | Change the port and restart if running. |
| `/dalamudmcp tools` | Print every registered tool name. |

## Limitations

**In-game verification has been done — once, on this machine, with a live client.** All 33 read-only tools were called
over the real endpoint (`http://127.0.0.1:18777/mcp`) with FFXIV running and a character logged in, and every
one returned real game data (or the correct "empty" answer for genuinely empty state — solo party, no FATEs,
no targets, idle gauge). Highlights: `get_local_player` returned the live character (name, level 100, job,
HP/MP, world, position); `get_game_objects` enumerated the player, a minion and training dummies with
distances; `get_job_gauge` decoded `BardGauge` live; `get_status_effects` located a `StatusManager` at
exactly the audited +9136 offset for both the player and a dummy; `read_object_memory` at +144 returned the
expected `ObjectKind` bytes; every Excel tool answered from the client's own data (CN locale — 火之碎晶,
吟游诗人, 强化药); `read_memory` at the module base returned the `MZ` header; `scan_signature` found real
matches; `get_module_info` reported the real client module.

That live session also caught **three real defects the offline suites could not see**, each fixed and
re-verified in game by rebuilding and letting Dalamud's dev-plugin auto-reload pick up the new DLL:

1. `get_job_gauge` threw `ArgumentException` inside `Enum.GetName` because the JSON-side `JValue` box was
   passed where a real CLR box of the enum's underlying type is required. A separate `BoxClr` helper now
   builds the true boxed value for reflection, and `Box` keeps producing `JValue` for JSON.
2. `get_game_data_sheet_info` crashed with `Could not determine JSON object type for type
   System.Reflection.RuntimePropertyInfo` — the `textColumns` list serialized raw `PropertyInfo` objects.
   It now emits property **names**.
3. `get_game_data_row` **always read row 0**: the reflection buffer passed to `TryGetRow` never boxed the
   requested row id, and on .NET 10 `MethodInfo.Invoke` silently converts a `null` argument for a `uint`
   parameter into `0` instead of throwing — a measured behaviour, not an assumption. Row 2 then returned
   row 0's data. The id is now boxed explicitly.

What was verified before that session, and still stands:

- The plugin project builds clean for x64 against Dalamud API level 15 with the assemblies from a real
  install (`0 errors, 0 warnings`).
- `tests\ProtocolSmokeTest` passes `47/47` against the real transport and protocol sources.
- `tests\BridgeSmokeTest` passes `22/22` against the built stdio bridge and the real server.
- `tests\LoadabilityCheck` passes `103/103` against the built plugin DLL and manifest, including a verdict from
  Dalamud's **own** `ServiceContainer`: it rebuilds the container offline (`RegisterInterfaces` is pure
  attribute reflection), installs one singleton key per non-scoped service type, and invokes Dalamud's private
  `FindApplicableCtor`. Guarded by four controls — one of which withholds the singleton keys and asserts the
  same constructor is then refused — so the result is a statement about Dalamud rather than about the harness.
  The offset audit re-derives every hand-copied FFXIVClientStructs offset from the installed library itself.
- `tests\McpInterop` passes `12/12` protocol checks, `2/2` negative-control checks and `11/11` real-schema checks
  with the **official** MCP SDK as the client — framing judged by somebody else's implementation, and all 36
  shipped schemas compiled against the JSON Schema 2020-12 meta-schema by the SDK's own `ajv`.
- `tests\PluginLoadTest` passes `93/93` running the shipped `Plugin` constructor out of game: the real load
  path executes, registers all 45 tools, binds the configured port, serves MCP over a real socket, validates every
  shipped tool schema and cross-checks it against what the handlers demand, honours the
  request-log and bearer-token settings end to end, runs every `/dalamudmcp` subcommand against the live
  listener, reads **real game data** through a Reflection.Emit-built data manager backed by this machine's
  installed `sqpack` files (item 1 resolves to 金币), reads and validates **real memory** in its own process,
  and unloads cleanly.
- The plugin DLL and manifest are produced in the build output and the project references the correct
  Dalamud and FFXIVClientStructs assemblies.

Not verified, and therefore not claimed: in-game behaviour beyond the single verification session described
above (one machine, one client build, one logged-in character in one zone — other locales, other jobs' gauges
beyond Bard, combat in progress, parties, dungeons and GPose were not exercised); that the offsets match a
**future** game client build (they match the client that ran today); and long-session behaviour such as
memory growth in the session store or the listener surviving many hours of polling.

**Other limitations:**

- `tests\LoadabilityCheck` and `tests\PluginLoadTest` locate the repository by walking up from their own
  binary (or honor a `DALAMUD_MCP_REPO` environment variable), and read Dalamud assemblies from
  `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`, so they only run as-is on a CN-launcher machine. Use the
  global launcher and they exit non-zero.
- The request log records the method, tool name, outcome and elapsed time — deliberately not argument
  bodies or result payloads, so it will not tell you *what* was passed to a call. Raw memory reads are
  logged by name only, and their arguments are not echoed anywhere.
- The plugin itself has no stdio transport; HTTP-capable clients connect directly and stdio-only clients go
  through `bridge\DalamudMcpBridge`.
- No mutating tools, so `AllowMutatingTools` currently has no effect on what is exposed.
- `get_game_objects` and similar enumeration tools read the whole object table and filter in memory, which is
  fine at normal object counts but not designed for tight polling loops.
- The plugin is not packaged for a plugin repository; `DalamudPackager` is not referenced.
- The session store keeps sessions in memory with a 2-hour idle cutoff, so a long-idle client must
  re-initialize.
