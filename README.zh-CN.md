# Dalamud MCP

[English](README.md) | 简体中文

一个 FINAL FANTASY XIV 的 Dalamud 插件，在回环端口上承载一个 [Model Context Protocol](https://modelcontextprotocol.io)
服务器，让 AI Agent 能够从运行中的游戏客户端读取实时状态。插件运行在游戏进程内部，因此可以读取客户端
自身使用的同一份内存：对象表、本地玩家、小队与联军、目标、FATE、货币、Excel 游戏数据，以及通过
[FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) 读取的任意经过校验的原始内存。MCP 客户端连接
`http://127.0.0.1:18777/mcp` 即可调用 36 个工具 —— 33 个只读，外加 3 个 UI 工具（可选开启的可变更工具），
用于打开和关闭游戏内的 addon 窗口。全部 33 个只读工具均已针对一个实时登录的客户端完成验证
—— 见[局限](#局限)。

## 构建

插件面向 `net10.0-windows7.0` 和 x64，引用本地 XIVLauncher 安装中的 Dalamud 程序集而非 NuGet 包。

```powershell
cd <仓库根目录>\src\DalamudMCP
dotnet build DalamudMCP.csproj -p:Platform=x64
```

`DalamudMCP.csproj` 会自动解析 `$(DalamudLibPath)`，优先国服安装，其次回退到国际服：

- `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
- `%APPDATA%\XIVLauncher\addon\Hooks\dev`

如果你的程序集在别处，可以覆盖它：

```powershell
dotnet build DalamudMCP.csproj -p:Platform=x64 -p:DalamudLibPath="D:\some\Hooks\dev\"
```

本仓库没有 `.sln`，插件项目本身也没有 NuGet 包引用 —— 只有 `bridge\` 和测试项目使用 NuGet。
`Dalamud.dll`、`Lumina`、`Lumina.Excel`、`Newtonsoft.Json` 和 `Dalamud.Bindings.ImGui` 都是对 dev 目录的
`Private=false` 引用，运行时使用游戏自带的副本。**FFXIVClientStructs 通过 Dalamud 获得** ——
`FFXIVClientStructs.dll` 引用自 Dalamud 自带的同一个 `Hooks\dev` 目录，这保证结构体偏移与已安装的
Dalamud 版本保持同步。清单声明 `DalamudApiLevel: 15`。

构建输出位于 `src\DalamudMCP\bin\x64\Debug\`，包含 `DalamudMCP.dll`、`DalamudMCP.json`、
`DalamudMCP.deps.json` 和 `DalamudMCP.pdb`。清单会自动复制到输出目录。

## 作为开发插件安装

构建输出已包含 Dalamud 需要的一切，因此可以直接就地注册 —— 不需要复制步骤：

```
src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll
src\DalamudMCP\bin\x64\Debug\DalamudMCP.json
src\DalamudMCP\bin\x64\Debug\DalamudMCP.deps.json
src\DalamudMCP\bin\x64\Debug\DalamudMCP.pdb
```

Dalamud 的开发插件支持由 `%APPDATA%\XIVLauncherCN\dalamudConfig.json`（国际服启动器为
`%APPDATA%\XIVLauncher\dalamudConfig.json`）驱动。两个条目很关键：

- `DevMode` 必须为 `true`。
- `DevPluginLoadLocations` 是一个 `{ "Path": "<dll 的绝对路径>", "IsEnabled": true, "Nickname": null }` 列表。
  添加一条 `Path` 指向你构建出的 `DalamudMCP.dll` 的条目即可。正常工作的安装中已有条目正是这个形状，
  所以再加一条就足以注册插件。

相关的 `DevPluginSettings` 字典以同一绝对 DLL 路径为键，携带每个插件的标志（`StartOnBoot`、
`NotifyForErrors`、`AutomaticReloading`、`WorkingPluginId`、`DismissedValidationProblems`）。这里说明其形状
仅供参考；插件不要求你手工编写它。

以这种方式注册的插件不需要单独的 `devPlugins` 文件夹 —— `DevPluginLoadLocations` 中的路径会被直接加载。
编辑 `dalamudConfig.json` 时游戏必须不在运行，因为 Dalamud 在退出时会重写该文件并覆盖你的修改。路径条目
示例：

```json
{
  "Path": "C:\\path\\to\\Dalamud-MCP\\src\\DalamudMCP\\bin\\x64\\Debug\\DalamudMCP.dll",
  "IsEnabled": true,
  "Nickname": null
}
```

加载后，插件会自动启动监听器（`Enabled` 和 `AutoStart` 默认均为 true），可用 `/dalamudmcp status` 检查。

## 验证它可用

`tests\ProtocolSmokeTest` 在没有游戏的情况下驱动真实的 MCP 服务器。它**编译实际的传输层源码** ——
`McpServer.cs`、`MiniHttp.cs`、`ToolRegistry.cs` 和 `Json.cs` 通过 `<Compile Include>` 原样引入自
`src\DalamudMCP\Mcp` —— 所以它是对运行中服务器的真实测试，而不是重新实现。这四个文件刻意不依赖
Dalamud，这正是该测试成为可能的原因。

```powershell
cd <仓库根目录>\tests\ProtocolSmokeTest
dotnet run --project ProtocolSmokeTest.csproj -p:Platform=x64
```

它在临时端口上启动服务器，通过真实的回环 HTTP 驱动它，并打印：

```
47/47 checks passed
```

任何检查失败时进程以非零码退出。它覆盖 `initialize` 握手与协商出的协议版本、带 schema 与注解的
`tools/list`、`tools/call` 的成功路径与两条错误路径（`ToolException` 与意外异常）、未知工具与未知方法拒绝、
可变更工具闸门（关闭时拒绝，打开后可见且可调用）、认证的开与关（bearer 头与 `?token=` 查询参数）、
无会话的 `POST /mcp` 拒绝、24 个并发调用各自返回自己的结果、可选的请求日志（默认静默，开启后每个请求
一行，注明工具名并报告结果，关闭后再次静默），以及干净的启动/停止/重启行为。

其中两项检查是对照实验，值得说明原因。这台机器**不会**一致地报告一个已关闭的回环端口：一个被启动、
停止且从未被连接的教科书式 `TcpListener`，会在 `ConnectionRefused` 和一直挂起直到超时的连接之间交替。
所以"停止后连接被拒绝"不是一个可实现的断言 —— 本测试的早期版本把服务器的结果与对照的结果相比较，
只是碰巧通过。探针现在区分"拒绝 / 挂起 / 接受"，断言停止后的端口**不在接受**连接，并先探测*存活*端口
以确认探针确实能看到监听器。正是这个正向对照让否定结果有意义。

这个测试证明协议与传输层可用。它无法证明任何游戏数据相关的事情，那需要一个运行中的客户端。

`tests\BridgeSmokeTest` 对 stdio 桥做同样的事。它在进程内承载真实的 `McpServer`，把构建出的
`dalamud-mcp-bridge.exe` 作为子进程启动，通过其 stdin 说以换行分隔的 JSON-RPC：

```powershell
cd <仓库根目录>\tests\BridgeSmokeTest
dotnet run --project BridgeSmokeTest.csproj -p:Platform=x64
```

```
22/22 checks passed
```

覆盖握手与会话捕获、通知抑制（通知必须不产生回复）、`tools/list`、参数与错误往返、畸形行的存活、
五个流水线请求各拿到自己的 id，以及 stdin EOF 时的干净退出。它还针对打开了 token 闸门的服务器把桥
多跑两次：一次**不带** `--token`（桥必须拒绝启动并说明缺少 token），一次**带上**（桥自己的握手与一次
工具调用都必须打通）。运行前先构建桥；测试在 `bridge\DalamudMcpBridge\bin` 下查找最新的
`dalamud-mcp-bridge.exe`，找不到则失败。

`tests\LoadabilityCheck` 检查**构建出的插件 DLL 与清单**中那些编译期不可见、在游戏里只表现为"插件加载
失败"且无有用细节的失败模式：畸形或不完整的清单、与已安装 Dalamud 不匹配的 `DalamudApiLevel`（Dalamud
会静默拒绝）、类型不是 Dalamud 服务类型的 `Plugin` 构造函数参数，以及重复的工具名（`ToolRegistry.Add`
在重复时抛异常，而注册发生在 `Plugin` 构造函数内部）。

```powershell
cd <仓库根目录>\tests\LoadabilityCheck
dotnet run --project LoadabilityCheck.csproj
```

```
103/103 checks passed
```

它直接加载插件程序集，并与国服 dev 程序集中 Dalamud 自己的类型表相比较，不链接任何源码 —— 它校验的
是交付的二进制而不是源码。

**它还用 Dalamud 自己的容器代码（而不是一份拷贝）回答了 DI 问题。** 本测试的早期版本对二十个构造函数
参数只能报告"类型存在"，因为 `ServiceContainer` 的接口映射看上去只在运行中的客户端里存在。其实不是：
`RegisterInterfaces` 是纯特性反射，`ValidateCtor` 从不解引用服务实例 —— 它只读取 `instances` 中的*类型*
加 `[ScopedService]` 特性。于是测试构建一个真实的 `ServiceContainer`，完全按照
`InitializeEarlyLoadableServices` 的方式为每个具体 `IServiceType` 调用 `RegisterInterfaces`，为每个非
scoped 服务类型安装一个单例键（`Task.FromResult<T>(null)` 就够了 —— 只检查键的类型），把一个
`DalamudPluginInterface` 替身作为 scoped 对象交给它，然后调用 Dalamud 自己的私有 `FindApplicableCtor`。
结论是 Dalamud 给出的，不是本项目对该规则的重新实现：

```
112 service types mapped, 83 singletons installed, 29 scoped left out | 19 interface, 0 singleton, 1 scoped
[PASS] Dalamud's own container accepts the plugin constructor
[PASS] the container probe is capable of rejecting a constructor
[PASS] the container probe still accepts a resolvable constructor
[PASS] the verdict depends on the services Dalamud registers
[PASS] every parameter is reachable through Dalamud's own resolution paths
```

四项中有三项是对照，其中两项的存在是因为这个探针的第一次尝试毫无价值。第一个对照用
`System.Version` 作为不可满足的类型；它因错误的原因通过 —— `Version` 有无参构造函数，`ValidateCtor`
对空参数列表直接返回 `true`，根本不查询任何服务 —— 所以探针的"拒绝"什么也没证明。替代者是一个在测试
内定义、恰好只有一个接受 `string` 的公共构造函数的类型，且检查额外断言该类型恰好暴露一个构造函数，
因为 `FindApplicableCtor` 在"拒绝不可满足的构造函数"和"根本没有构造函数可考虑"两种情况下都返回 `null`。
第四个对照是因果性的那个：它用**相同**的接口映射重建容器但扣住单例键，并断言同一个构造函数随后被
拒绝。没有它，"83 个单例已安装"就只是装饰，结论可能是重建过程的产物而不是关于 Dalamud 的陈述。

**偏移审计。** 直接结构体工具（`get_job_gauge`、`get_status_effects`）通过手工抄录的 FFXIVClientStructs
字段偏移读取游戏内存。一次移动了字段的库更新不会抛异常 —— 它会静默读到另一个成员的字节。所以测试
从已安装的 `FFXIVClientStructs.dll` 本身重新推导每个声明的常量（用特性偏移而非 `Marshal.OffsetOf`，因为
这些类型带有 `Marshal` 无法布局的指针成员）：`BattleChara.StatusManager` 位于 +9136、`StatusManager` 的
owner、条目数组、特殊状态计时器、有效状态计数（+984）与附加标志字节（+985）、其 `StructSize`（992）、
`GameObject.ObjectKind`（+144），以及 `JobGaugeManager` 的 `ClassJobId`（+88）与职业量表 union（+8，
并验证每个 union 成员共享同一偏移）。它还交叉核对 `ClassJobId` → 量表结构体表：库自带的每个具体量表
结构体都被覆盖，没有一个是凭空捏造的，且覆盖的职业 id 恰好是已知拥有专属量表的 21 个战斗职业。
FFXIVClientStructs 更新现在会在这里按名字失败，而不是在游戏里损坏一次读取。

**本地化审计。** `Localization.cs` 中的两套语言表检查 key 对等 —— 每个 key 必须在两套表中都能解析，
所以一个只加进英文而忘了中文（或反过来）的字符串会让套件失败，而不是让一种语言的用户永远静默看到
英文回退。

注入一个伪参数（`System.Net.Http.HttpClient`）会让两个真实检查失败而四个对照全部通过 —— 这个判别器
就是把插件缺陷与损坏的探针区分开的东西。先构建插件：测试在固定绝对路径查找
`src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll`，缺失时以退出码 2 结束。它仍然无法证明的：服务实例本身
能否在游戏内构造，或它们读取的游戏侧状态是否就绪。

`tests\PluginLoadTest` 走得更远，**在游戏外运行交付的 `Plugin` 构造函数**。上面三个测试从不执行插件
类型：两个用桩游戏线程重托管传输层，一个只读元数据。这个测试按路径加载 `DalamudMCP.dll`，用
`DispatchProxy` 合成的二十个 Dalamud 服务实例化 `DalamudMCP.Plugin`，然后通过真实 TCP 与监听器说 MCP。

```powershell
cd <仓库根目录>\tests\PluginLoadTest
dotnet run --project PluginLoadTest.csproj -p:Platform=x64
```

```
93/93 checks passed

The shipped plugin loads, registers its tools, binds its port, serves MCP, and unloads.
```

> 数量取决于本机是否能找到游戏数据：有真实数据管理器时为 `93/93`，没有时为 `87/87`。多出的六项是
> 真实游戏数据断言与"无逃逸异常"契约检查。

它端到端地走了一遍真实的加载路径：配置加载与 `Sanitize`、服务对象图的构建、全部五个工具集注册进真实
注册表、ImGui 窗口构建、`UiBuilder.Draw` / `OpenConfigUi` 订阅、`/dalamudmcp` 命令注册、HTTP 监听器绑定
配置的端口、处理器经 `GameThread` 运行并返回格式良好的 JSON，以及一个干净的 `Dispose`（取消两个事件
订阅、移除命令、停止监听器）。这是"插件会加载"所能给出的最强游戏外证据 —— 而且加载本身后来也在
游戏内得到了确认（见[局限](#局限)）。

三个配置项是**通过交付的插件**断言的，而不是对手工搭的服务器：请求日志（`LogRequests`）、bearer
token（`AuthToken`）和端口。前两项在插件构造之后设置到配置实例上，这同时证明插件是逐请求读取它们
而不是在启动时快照 —— 这正是设置窗口里切换它们无需重载插件就生效的原因。在此之前，`Config.AuthToken`
只作为*字段*被验证过；没有任何检查确认插件把它转发进了服务器的 `TokenProvider`，一处接线错误本可能
让端口敞开而 UI 却声称它受保护。

`/dalamudmcp` 聊天命令是**被运行**的，而不仅是检查了注册。`CommandInfo.Handler` 是公共属性，测试把
真实的处理器委托取出来并按 Dalamud 的方式调用 —— 这意味着 `status`、`stop`、`start`、未知子命令、
`port <n>` 与越界 `port` 都针对存活监听器与活动配置被演练过。断言的是后果而不仅是输出：stop 必须
真的停止服务，start 必须再次服务 MCP，`port <n>` 必须*立即*移动监听器（释放旧端口并持久化设置），
越界端口必须被拒绝**且**不改变任何东西 —— 一个手滑不能把监听器静默挪到某个不可用的位置。注册了
处理器并不说明它的函数体可用，而这个命令是用户不手改配置文件时唯一的启停/改端口控制面。

内存工具在这里也是对**真实内存**验证的。`MemoryProbe` 通过 `ReadProcessMemory(GetCurrentProcess(), ...)`
读取当前进程，而加载测试在自己的进程内承载插件，所以它可以钉入一个已知字节模式并断言工具原样返回
这些字节。安全防护 —— 它存在的理由是坏指针本来会触发击杀整个游戏客户端的访问违例 —— 通过读取地址
`0x1` 并要求得到一句有措辞的拒绝来检查；这条断言能写出来本身就是防护生效的证据。

全部 36 个交付工具的 schema 都通过真实 socket 的 `tools/list` 校验：每个 `inputSchema` 必须是带 object 型
`properties` 的 JSON 对象，每个属性必须携带 JSON Schema 七个合法名之一的 `type`，每个 `array` 必须说明其
`items`，`required` 里的每个名字都必须真的被声明。加这条检查是因为它立刻抓到了一个真实缺陷：
`read_pointer_chain` 把它的 `offsets` 参数声明为

```json
{ "type": "array of integer" }
```

这不是合法的 JSON Schema 类型，也没有 `items` —— 严格的客户端或生成的绑定会拒绝整个工具。调用点的拼写
保留（在 C# 里更易读），`Json.ApplyType` 现在在线上把它展开为
`{"type":"array","items":{"type":"integer"}}`。曾临时回滚修复并确认检查以
`read_pointer_chain.offsets: type "array of integer" is not a JSON Schema type` 失败，验证了这条检查有效。

第二个缺陷在同一参数的*描述*里，写着 `e.g. [0x10, 0x1C0]`。JSON 没有十六进制字面量，照抄该示例的
Agent 会发出畸形请求 —— schema 在教错误的输入。描述现在展示合法 JSON，类型是合法的并集
`{"type":"array","items":{"type":["integer","string"]}}`，因为处理器确实同时接受两种形式
（`src\DalamudMCP\Tools\MemoryTools.cs:687` 的 `ParseOffset` 接受 `0x` 前缀字符串、纯十六进制数字符串和
十进制）。声明并集不是装饰：测试套件用 `["0x10"]` 调用 `read_pointer_chain`，断言十六进制转储*起始于*
标记处，而指针被故意存放在标记前 16 字节 —— 把 `"0x10"` 当十进制 10 读的解码器会提前 6 字节落地并被
抓住。这就是"schema 声称接受十六进制"与"处理器真的做到了"之间的区别。

同一段落还把 schema 与行为交叉核对：每个处理器自己的"缺少必需参数：X"消息在扫描期间被捕获并与
`required` 列表比较，所以 schema 称可选而处理器实际索要的参数会构成失败。比较覆盖 8 处处理器索求。
接受*多选一*参数的处理器（"提供 itemId 或 name"）被刻意不交叉核对 —— 那些情况下没有哪个单一参数
缺失，猜测该检查哪个会捏造出一个失败。

### 由独立客户端裁决组帧

上面的每个套件说的都是本项目作者手写的 JSON-RPC。这是一个真实的盲区：如果某个组帧细节被误读 ——
放错位置的会话头、只有本服务器才有的 `initialize` 结果形状、Agent 的 SDK 无法解析的工具结果 —— 手写
测试会与服务器自身的错误达成一致并照常通过。`tests\McpInterop` 消除了它：用**官方** MCP SDK
（`@modelcontextprotocol/sdk`，TypeScript 实现，锁定 `1.30.1`）驱动真实服务器，让它裁决线路。

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

宿主（`tests\McpInterop\host`）链接四个 `src\DalamudMCP\Mcp\*.cs` 文件，它们完全没有 Dalamud 引用 ——
正是这条接缝让进程外协议测试成为可能。官方客户端完成 `initialize`、读回服务器身份、解析 `tools/list`、
往返一次工具调用、把工具错误作为 `isError` 接收而非传输失败，并且之后继续工作。它还被给了那个并集
数组 schema（曾有真实缺陷的形状）去解析，因为这正是客户端可能呛住的细节类型。

该套件的第四阶段校验全部 36 个**交付** schema，而不是合成的：`PluginLoadTest` 在设置了
`DALAMUD_MCP_DUMP_TOOLS` 时导出它的 `tools/list` 载荷，该载荷经过 SDK 声明的 `ToolSchema`，再用 SDK 内置
的 `ajv` 对照 JSON Schema 2020-12 元 schema 编译（36 个 schema —— 参数总数以下一次完整 interop 运行的实测为准，schema 变更时会重新测量）。

"要求检查必须能够失败"教会了我们两件事：

**官方 SDK 自己的工具 schema 校验宽松到抓不住本项目的缺陷。** 它的 `ToolSchema` 把 `inputSchema` 类型定
为带 `"object"` 型 `type` 与 `properties` 记录的对象，其值只被检查为*对象* —— 从不检查属性的 `type` 是否为
JSON Schema 的合法名。把原始 `{"type":"array of integer"}` 缺陷重新注入真实的 36 工具载荷，它照样通过。
因此标签被重写为只陈述它证明的内容（官方 SDK 接受全部 36 个交付工具定义为 tools/list 输出），并加入了
ajv 元 schema 检查，它以一条独立的消息拒绝：`type must be JSONType or JSONType[]: array of integer`。

**因错误原因而通过的阴性对照不是对照。** `negative.mjs` 把官方客户端指向一个刻意不符规的服务器并要求
它失败。有一次它"通过"是因为客户端根本连不上服务器：Windows 从 1024–15000 分配临时端口，与 Node 的
`fetch` 在*连接之前*就拒绝的 WHATWG 端口黑名单重叠（`1719` 在列），宿主绑到了一个任何 Node 客户端都
不会对话的端口。直接测量：`1719` 上有监听器时，`fetch` 仍以 `cause: bad port` 失败。一个 `fetch failed`
被当成了成功的协议拒绝。修了三处：宿主现在重绑直到落在黑名单之外（`host\Program.cs`）、`interop.mjs`
拒绝这类端口并给出诊断而不是裸抛 `fetch failed`，以及 —— 最重要 —— 对照现在断言*原因*：它要求一个
点名 JSON-RPC 字段的 `ZodError`（路径 `["jsonrpc"]` 处的 `invalid_union`），若拒绝来自传输失败则判失败。
曾通过把假服务器强制放到 `1719` 验证，对照现在报告 `0/2` 并输出：

```
[FAIL] the official SDK REJECTS a non-conforming server -> rejected for the wrong reason
       (TypeError: fetch failed) - the client may never have reached the server, which would make
       this control vacuous
```

`validate-real-tools.mjs` 出于同样的原因自带自我证明的对照：它把原始缺陷重新注入同一真实载荷并要求
元 schema 拒绝，一旦不再拒绝就以
`the meta-schema accepted an illegal type name - the positive check is vacuous` 失败。不能失败的测试不是证据。

### 数据管理器是真的，不是桩

二十个服务中有十九个是惰性的 `DispatchProxy` 桩。数据管理器不是，因为在那里桩无法被做成可用的。

`DispatchProxy` 不会把它生成的方法重写复制泛型参数约束，所以重写

```csharp
ExcelSheet<T> GetExcelSheet<T>() where T : struct, IExcelRow<T>
```

会在**生成的函数体内部**死于

```
TypeLoadException: GenericArguments[0], 'T', on 'Lumina.Excel.ExcelSheet`1[T]' violates the constraint of type parameter 'T'
```

处理器根本到不了。一个七对照的独立探针精确分离出了这条规则：仅 F 约束没问题（原样返回，或作为参数
使用），约束泛型返回类配普通 `struct` 约束也没问题，只有**组合** —— F 约束的类型参数流入以同样方式
约束的类 —— 才失败。真实签名正是那个组合。所以在惰性代理下，每个触及表数据的工具都报告
`TypeLoadException`，无论插件是否正确，而测试在撒谎说它们为何失败。

`tests\PluginLoadTest\RealDataManager.cs` 用 `Reflection.Emit` 修好了它 —— 反射发出**确实**复制约束。它
发出一个实现 `Dalamud.Plugin.Services.IDataManager` 的类型，并且 —— 在找到本机游戏安装时 —— 通过 Lumina
对着该安装的 `sqpack` 文件构造它，**不需要游戏进程在运行**直接读取。结果是表工具对着真实游戏数据被
演练：

```
  [note] data manager is REAL, reading C:\Program Files\上海数龙科技有限公司\最终幻想XIV\game\sqpack
  [note] Item row 1 read through the emitted manager: 金币
  [PASS] get_item(1) returned the real CN item name
  [PASS] list_game_data_sheets reports the game's real sheet count
  [PASS] no sheet-reading tool escapes an exception against real game data
```

最后一项是惰性代理曾使它无法被写出的检查。它断言没有任何读表工具报告逃逸异常 —— 这是真实缺陷信号，
而不是测试的假象。

sqpack 目录按以下顺序定位：`DALAMUD_MCP_SQPACK` 环境变量、四个已知安装路径，然后是 `dalamud.log` 中的
`Lumina is ready: <path>` 行（最可靠的信号，因为这是启动器实际解析出的路径）。都找不到时测试会说明并
回退到惰性代理而不是失败 —— 协议检查照常运行，真实数据检查与无逃逸异常检查被跳过。

它刻意**不**声称什么，并诚实地报告：

- **不**声称二十个服务*实例*能在游戏内构造，或它们读取的游戏侧状态就绪。`LoadabilityCheck` 现在确实
  另行解决了"Dalamud 的容器是否会把那些服务交给构造函数"这个独立问题，方法是离线重建真实
  `ServiceContainer` 并调用 Dalamud 自己的 `FindApplicableCtor`；此处仍未验证的是服务自身的运行时行为。
- **不**声称每个处理器都返回有意义的游戏数据。十九个服务仍是惰性的，触及对象表、内存、sig 扫描器或
  游戏状态的处理器看到的是中性值并报告有措辞的错误（扫描输出会准确显示是哪些、有多少）。表层是例外，
  它是真的。
- **不**声称任何挂钩存活客户端进程的事情。那个缺口后来已另行关闭 —— 见[局限](#局限)中的游戏内验证
  说明 —— 但本测试本身仍然只在游戏外运行，它诚实的范围就是这里写的这些。

先构建插件；与 `LoadabilityCheck` 一样，它从自身二进制向上查找 `src\DalamudMCP` 来定位仓库，并从
`%APPDATA%\XIVLauncherCN\addon\Hooks\dev` 读取 Dalamud。

## 配置

用 `/dalamudmcp`（或插件安装器的配置按钮）打开设置窗口。窗口标题是 "Dalamud MCP"。

| 设置 | 默认 | 含义 |
| --- | --- | --- |
| `Enabled` | `true` | 监听器总开关。 |
| `AutoStart with the plugin` | `true` | 插件加载时启动监听器。 |
| `Port` | `18777` | TCP 端口。钳制到 1024-65535。只绑定 `127.0.0.1`。 |
| `Framework timeout (s)` | `10.0` | 工具调用等待游戏框架线程多久后失败。钳制到 0.5-120。 |
| `Allow mutating tools` | `false` | 改变游戏状态的工具是否对客户端可见、可调用。 |
| `Auth token` | 空 | 共享密钥。为空则禁用认证。 |
| `Max object results` | `200` | 单次查询返回对象数上限。钳制到 1-5000。 |
| `Max memory read (bytes)` | `65536` | 单次原始内存读取上限。钳制到 16-1048576。 |
| `Log every MCP request` | `false` | 把每个 JSON-RPC 请求及其结果（方法、id、工具名、耗时 ms）写入插件日志。实时读取，切换立即生效。默认关闭，因为轮询的 Agent 可能每秒发起大量调用。 |
| `Language` | `auto` | 界面与 `/dalamudmcp` 反馈语言。`auto` 跟随游戏客户端语言；也可选 `English` 或 `简体中文`。立即生效，无需重载。 |

端口与超时改动需要重启监听器；窗口会说明并提供"Restart listener"按钮。其他编辑立即生效。设置在控件
失焦后持久化，所以拖动滑块不会每帧重写配置文件。窗口还提供"Save now"、"Start listener" /
"Stop listener" / "Restart listener" 和"Open config folder"。

插件自带英文与简体中文字符串（`src\DalamudMCP\Localization.cs`）。默认 `Language: auto` 下，中文游戏
客户端显示中文标签、命令反馈和绑定失败的 toast；英文客户端显示英文。语言表编译进插件 DLL（无附属
资源），缺失 key 回退英文，且 `tests\LoadabilityCheck` 会在 key 只存在于一套表中时大声失败。

`AllowMutatingTools` 默认**关闭**，因为对 Agent 的写访问应被显式授予，而不是作为安装插件的副作用附带
得到。闸门在两处强制：可变更工具被从 `tools/list` 过滤掉，即使客户端知道名字，`tools/call` 也会以解释性
错误拒绝。打开设置还会把标签从"read-only (recommended)"换成"agents may change game state"警告。

**注意：** 闸门已完整实现并测试，现在第一批可变更工具已在它之后交付：三个 UI 工具（`open_addon`、
`close_addon`，外加只读的 `get_addon_state`）可以打开和关闭游戏内的 addon 窗口，例如背包、军械库或
任务搜索器。`open_addon` 与 `close_addon` 被标记为可变更，因此在 `AllowMutatingTools` 打开之前，它们在
`tools/list` 中不可见、`tools/call` 会拒绝；其余工具集（33 个工具）仍然只读，也依旧没有聊天命令或输入
注入工具。

## 连接 Agent

所有端点都在 `127.0.0.1` 上：

| 端点 | 方法 | 用途 |
| --- | --- | --- |
| `/mcp` | POST / GET / DELETE | Streamable HTTP 传输。主端点。 |
| `/sse` | GET | 旧版 HTTP+SSE 传输（协议 2024-11-05）。 |
| `/messages?sessionId=...` | POST | 旧版传输的消息汇，与 `/sse` 配对。 |
| `/health` | GET | 状态：工具数、协议版本、会话数、认证与可变更工具是否开启。 |
| `/tools` | GET | 平铺工具目录（名称、标题、描述、可变更标志）。 |
| `/` | GET | 服务器信息与传输映射。 |

服务器宣告协议版本 `2025-06-18`、`2025-03-26` 和 `2024-11-05`。

**`initialize` 之后必须有 `Mcp-Session-Id`。** 只有 `initialize` 请求可以在不带它的情况下发送。之后每次
POST 到 `/mcp` 都必须携带握手返回的 `Mcp-Session-Id` 头，否则服务器以
`{"error":"session_required"}` 应答 `400`。服务器不再认识的 id 返回 `404` 与
`{"error":"session_not_found"}`，让客户端重新初始化而不是死循环。符合规范的 MCP 客户端会自动处理。

如果设置了认证 token，每个请求都必须携带 —— 包括 `/health` 与 `/tools`。只有 `OPTIONS`（CORS 预检）与
`GET /` 可以不带它应答：

```
Authorization: Bearer <token>
```

也接受 `?token=<token>` 查询参数作为替代，方便浏览器标签页或无法设置头的客户端。

支持 HTTP 的 MCP 客户端的通用配置如下 —— 具体键名请查阅你的客户端文档，各家不同：

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

未设置认证 token 时，`headers` 块可以整个省略。

### 仅支持 stdio 的客户端

有些客户端启动子进程并通过 stdin/stdout 说 JSON-RPC 而非 HTTP。本仓库正好为这种情况提供一个桥：
`bridge\DalamudMcpBridge`。它是一个小型的无依赖控制台程序，在 stdin 上读以换行分隔的 JSON-RPC，把每条
消息转发给插件的 HTTP 端点 —— 所以把客户端的 `command` 指向**桥**，永远不要指向插件 DLL。

```powershell
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
```

然后用构建出的可执行文件配置客户端：

```json
{
  "mcpServers": {
    "dalamud": {
      "command": "C:\\path\\to\\Dalamud-MCP\\bridge\\DalamudMcpBridge\\bin\\Release\\net10.0\\dalamud-mcp-bridge.exe"
    }
  }
}
```

桥接受 `--port <n>`、`--url <http://host:port>`、`--token <token>` 和 `--verbose`，也从环境读取
`DALAMUD_MCP_PORT`、`DALAMUD_MCP_URL` 和 `DALAMUD_MCP_TOKEN`，让这些标志不必出现在客户端配置里。
什么都不给时解析为 `http://127.0.0.1:18777`。

它处理两件裸管道做不到的事：

- **会话捕获。** 插件在握手期间于 `Mcp-Session-Id` *响应头*中下发会话 id。stdio 客户端只能看到 JSON
  体，永远无法观测或回显该 id。桥捕获它并附加到之后每个请求。
- **重新握手。** 游戏或插件重载后，会话 id 过期。桥检测到 `session_required` 拒绝，用自身的
  `initialize` 铸造新会话、重放握手通知，并把原始请求重试一次 —— 重载不需要重启客户端。

它还在认证不匹配时**快速且大声地失败**。打开流之前先探测 `GET /health`，非成功应答是致命的而不是
继续带过的东西。`401` 会说明问题是缺 token 还是 token 错，并指出修复方法：

```
[bridge] the plugin requires a bearer token and none was supplied.
[bridge] pass --token <token> (or set DALAMUD_MCP_TOKEN) to match the plugin's AuthToken setting.
```

这个区分由测试套件检查，因为它取代的失败模式是静默的：`HttpClient` 对 `4xx` 不抛异常，没有正确
token 的桥过去会记录 `plugin health: 401`、"成功"启动，然后每个请求都失败且毫无解释。

stdout 只承载协议消息；所有诊断走 stderr，所以 `--verbose` 可以安全地常开。

接线客户端之前先确认服务器在线：

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
Invoke-RestMethod http://127.0.0.1:18777/tools | ConvertTo-Json -Depth 4
```

## 工具参考

36 个工具：33 个只读，外加 3 个 UI 工具（`open_addon` 与 `close_addon` 是可变更工具，在
`AllowMutatingTools` 打开前隐藏；`get_addon_state` 只读）。名称与 `tools/list` 中完全一致。

### 客户端与会话

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_client_state` | 登录状态、区域/领地（id 与名称）、地图、分线、语言、PvP/PvP-排除-Den/GPose 标志、空闲状态、当前阻塞的 condition 标志、本地玩家对象是否已加载、框架更新增量、上次更新时间、进程 id 与指针宽度。推荐的首个调用 —— 登出时多数其他工具报告不了有用的东西。 | 无 |
| `get_local_player` | 你控制的角色：身份、世界、职业、等级、HP/MP、位置、状态，以及来自 `PlayerState` 的账号级身份（content id、主世界/当前世界、种族、部族、性别、有效等级、等级同步与 grand company）。登出时返回 `loggedIn=false`；角色仍在加载时返回 `loggedIn=true` 且 `playerLoaded=false`。 | 无 |
| `get_player_attributes` | 通过 `IPlayerState.GetAttribute` 获取的角色面板属性，外加基础力量/灵巧/耐力/智力/精神/信仰。只返回非零属性。 | 无 |
| `get_job_levels` | 每个职业的等级、经验与解锁标志，并标记当前职业。既未解锁也未练过的职业被跳过。 | 无 |
| `get_conditions` | 当前为真的每个 `ConditionFlag`，外加 `inCombat`、`mounted`、`crafting`、`gathering`、`betweenAreas`、`watchingCutscene`、`occupiedInEvent` 与 `boundByDuty` 的显式布尔。 | 无 |
| `get_currency` | 金币、部队金币、军票、同盟军票、狼印战章、金碟币、雇员金币、周限神典石，外加具名货币容器道具（神典石、工票、聚类材料）。 | 无 |
| `get_titles_and_achievements` | 传 `titleId` 或 `achievementId` 检查单个条目（成就的名称、描述、点数/完成度；称号的名称、前缀标志、解锁状态）。都不传时只返回表条数、成就/称号列表是否已加载与一条提示。 | `titleId`、`achievementId` |
| `get_unlock_state` | 某个具体事物是否已解锁。 | `type`（必需，23 个值：`mount`、`minion`、`emote`、`orchestrion`、`tripleTriadCard`、`recipe`、`instanceContent`、`action`、`generalAction`、`craftAction`、`trait`、`buddyAction`、`buddyEquip`、`glasses`、`ornament`、`title`、`howTo`、`quest`、`leve`、`achievement`、`aetherCurrent`、`classJob`、`unlockLink`）、`id`（必需） |
| `get_aetherytes` | 可传送的以太之光及其金币费用、领地与收藏状态。 | `max`（默认 200） |

### 游戏对象

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_game_objects` | 你周围加载的对象：玩家、NPC、敌人、宠物、坐骑、宝箱、以太之光。 | `objectKind`、`nameContains`、`maxDistance`、`detailed`（默认 true）、`max`（默认 200） |
| `find_game_object` | 按 id 或精确名称查一个对象，外加共享该名称的替代项。 | `entityId`、`gameObjectId`、`name` |
| `get_party` | 小队或联军成员及其职业、等级、HP/MP、世界、领地与状态。 | 无 |
| `get_targets` | 当前、焦点、软、鼠标悬停、鼠标悬停名牌、上一个与 GPose 目标。 | 无 |
| `get_fates` | 活跃 FATE 及其等级范围、进度、剩余时间与位置。 | `fateId` |
| `get_nearby_enemies` | 半径内的战斗 NPC，按距离排序，只返回存活的。 | `radius`（默认 30）、`max`（默认 50） |
| `get_object_table_info` | 原始对象表地址与每个数组的条目数。 | 无 |

需要已加载角色的工具在标题画面时返回 `{"available": false, "reason": "not logged in"}` 而不是失败 ——
调用永远安全。

### 游戏数据（Excel）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `search_game_data` | 在一张表的文本列中搜索（Item、Action、Status、Quest、Mount、TerritoryType、World、ClassJob 等）。先用 `get_game_data_sheet_info` 看哪些列可搜索。 | `sheet`（必需）、`query`（必需）、`exact`（默认 false）、`max`（默认 25，上限 200） |
| `get_game_data_row` | 按 id 取一行，返回每个可读列（每行上限 120 列；跳过 `ExcelPage` 与 `RowOffset`，空值省略）。行引用以 `{"rowId": n}` 返回，不展开。 | `sheet`（必需）、`rowId`（必需） |
| `list_game_data_sheets` | 表名，可过滤。超过一千张表，请过滤。 | `filter`、`max`（默认 200，上限 2000） |
| `get_game_data_sheet_info` | 表名、完整行类型、客户端是否已加载该表、行数、带索引/类型/偏移的列数、可搜索的文本列，以及属性数。 | `sheet`（必需） |
| `get_item` | 物品名称、单数形、描述、物品等级、装备等级、稀有度、物品动作、UI/搜索分类、职业分类、堆叠上限、HQ 标志、不可交易标志、中/低档商店价、收藏品标志。 | `itemId`、`name`（精确匹配，除非 `contains`）、`contains`（默认 false） |
| `get_action` | 技能名称、描述、职业缩写、职业技能等级、咏唱/复唱时间（单位 100ms）、射程、效果范围、主/副消耗类型与数值、类别、目标区域、PvP 与玩家技能标志。描述在独立的 `ActionTransient` 表上，可能缺失。 | `actionId`、`name`（精确） |
| `get_status` | 状态名称、描述、图标、最大层数、部队buff标志、类别、驱散/注视/永久标志、小队列表优先级与移动/技能锁。 | `statusId`、`name` |
| `get_territory` | 区域名、地名、zone 与 region 名称、地图、内容查找器条件、以太之光、资料片、PvP 区域标志与坐骑/隐身许可。 | `territoryId`、`name`（不区分大小写的子串） |
| `get_class_job` | 职业/CLASS 定义：名称、缩写、英文名、类别、初始等级、排队标志、灵魂水晶与解锁任务。省略 id 列出全部 —— 把职业 id 映射到名称的最便宜方式。 | `classJobId` |

### 原始内存

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `read_memory` | 按绝对地址读取，十六进制+ASCII 转储或标量。先对 OS 区域映射校验。十六进制转储上限 4096 字节。 | `address`（必需，十六进制字符串）、`format`（默认 `hexdump`）、`length`（hexdump 默认 256 / string 1024 / 其余为格式大小） |
| `query_memory_region` | 地址的 OS 报告状态：已提交、可读、可写、可执行、保护属性、所属区域。带 `length` 时最多遍历 64 个区域，跨页边界的缓冲区也能完整描述。 | `address`（必需）、`length` |
| `read_pointer_chain` | 从静态基址跟随多级指针：解引用、加偏移、重复，最后可选解码一个值。返回每个中间地址，坏掉的步骤一目了然。 | `address`（必需）、`offsets`（必需，最多 16 级）、`valueFormat` |
| `get_module_info` | 游戏模块名称、基址与大小、Dalamud 搜索基址、扫描器是否在副本上运行、`.text` / `.data` / `.rdata` 节基址与大小，以及节偏移（据此判断本构建报告的是绝对还是相对地址）。 | 无 |
| `scan_signature` | 通过 Dalamud 签名扫描器做带 `??` 通配符的字节模式扫描。 | `signature`（必需）、`section`（默认 `text`，另有 `data`、`rdata`、`module`）、`maxResults`（默认 1，0 为全部） |
| `read_object_memory` | 在游戏对象地址加偏移处读取 —— 从带类型对象工具到原始字节的桥。按 `entityId`、`objectIndex`、`localPlayer` 解析对象，或直接传绝对 `address`。允许负偏移，可在结构体内反向遍历。 | `entityId`、`objectIndex`、`localPlayer`、`address`、`offset`、`length`（默认 64）、`format` |

### 直接结构体（FFXIVClientStructs）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_job_gauge` | 直接从 `JobGaugeManager` 内存读取的实时职业量表，覆盖 Dalamud 托管 API 不暴露的量表状态（可注入服务中没有量表访问器）。报告 `classJobId`，然后在管理器的 union 中定位该职业的专属量表结构体并解码每个 `[FieldOffset]` 字段 —— 包括渲染为命名位的 `BitFieldAttribute` 位段 —— 外加原始字节。没有专属量表的职业（秘术士、忍者、青魔）返回 `hasDedicatedGauge=false`。 | 无 |
| `get_status_effects` | BattleChara 的原始 60 槽 `StatusManager`，按结构体偏移读取而非通过 Dalamud 的 `StatusList`：owner 地址、附加标志字节、特殊状态计时器/方向浮点，以及每个状态条目（id、参数、剩余时间、来源对象 id、表名称/描述/层数）。像 `read_object_memory` 一样解析目标，或传绝对 `address`。 | `address`、`entityId`、`objectIndex`、`localPlayer`、`max`（默认 60） |

这两个工具在本插件其他任何地方都以同样方式降级：若 FFXIVClientStructs 的静态地址解析器尚未初始化
（插件在游戏就绪前加载），工具返回 `{"available": false, ...}` 及原因，绝不抛异常。

### UI（可变更，需 opt-in）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `open_addon` | 通过拥有该窗口的 agent 的 `AgentInterface.Show()` 打开一个游戏内 addon/系统窗口 —— 与游戏自身 UI 走同一条路径。addon 名称必须来自约 66 个已验证 agent 的固定允许列表（背包、军械库、情感动作列表、任务日志、成就、坐骑/宠物图鉴、乐团演奏乐谱、传送、任务搜索器、套装、系统设置、货币、雇员、部队等）；未知或不可打开的名称会被拒绝而不是猜测。 | `addon`（必填，允许列表枚举） |
| `close_addon` | 通过 `AgentInterface.Hide()` 关闭同一个允许列表中的 addon 窗口。关闭本就未打开的窗口是无操作而非错误。 | `addon`（必填，枚举） |
| `get_addon_state` | 只读普查：允许列表中哪些 agent 当前处于活动状态。报告 `active` 与 `inactive` 名称列表。 | 无 |

这些是本插件第一批可变更工具：`open_addon` 与 `close_addon` 以可变更标志注册，因此在 `AllowMutatingTools`
关闭（默认）时，它们会从 `tools/list` 中被过滤、`tools/call` 会拒绝。失败（agent 模块未初始化、agent 不可
打开）像工具集其余部分一样返回结构化 `{"available": false, "reason": ...}` 而不是抛异常。

`read_memory` 与 `read_object_memory` 的合法 `format` 值：`hexdump`、`bytes`、`u8`、`u16`、`u32`、`u64`、
`i8`、`i16`、`i32`、`i64`、`f32`、`f64`、`bool`、`string`、`utf16`、`pointer`。

地址以 `"0x7FF6A1B2C3D4"` 这样的十六进制字符串发出和接受，因为 JSON 数字在 2^53 之后丢失整数精度，而
64 位指针经常超过它。解析器也接受结尾 `h`、下划线和纯十进制，但传带引号的 `0x` 字符串是最可靠的形式。

## 安全模型

**默认只读。** 36 个工具中的 33 个是只读状态；没有一个写入。可变更闸门被强制，注册在它后面的只有打开和
关闭 addon 窗口的两个 UI 工具 —— 在 `AllowMutatingTools` 打开前它们不可见。依旧没有聊天命令或输入注入
工具。

**仅回环。** 监听器直接以 `TcpListener` 绑定 `IPAddress.Loopback`（`127.0.0.1`），而非 `HttpListener`。这
避免了 HTTP.SYS 的 URL-ACL 要求 —— 无需提权或 `netsh` 预留 —— 也意味着服务器永远无法从网络访问。不要
通过代理或端口转发暴露该端口；服务器没有 TLS，也没有针对那种暴露的任何防护。

**认证 token 是唯一的访问控制。** 在回环绑定内，任何本地进程都能到达该端口，所以 token 为空时你信任的
是机器上的每一个程序。在意就设一个。闸门覆盖每个返回数据或作用于游戏的端点。唯一的例外是 `OPTIONS`
（CORS 预检）与 `GET /`（固定不变的传输宣告，只列出 URL，不含游戏数据，也不随你的配置变化），它们
被设计为无需认证即可应答 —— 见上面的端点小节。其他一切（包括 `/health` 与 `/tools`）在设置了 token 时
都要求 bearer 头。

**原始内存读取不会让客户端崩溃。** 这是最重要的保证，也是 `MemoryProbe` 存在的理由：Agent 可以要求
任意地址，而游戏进程内的坏指针解引用会是击杀整个客户端的访问违例。每次读取前 `MemoryProbe` 调用
`VirtualQuery`，除非页面已提交且可读否则拒绝：

```csharp
var readable = mbi.State == MemCommit && (protect & PageNoAccess) == 0 && (protect & PageGuard) == 0;
```

读取随后被钳制到区域末尾，越界请求返回可读部分而不是触发故障，拷贝本身通过
`ReadProcessMemory(GetCurrentProcess())` 完成。不可读地址产生一条描述问题的正常工具错误。

**每次工具调用都在框架线程上运行。** 游戏内存只在客户端更新循环运行时才稳定，所以 `GameThread` 通过
`IFramework.RunOnFrameworkThread` 把每个处理器封送到框架线程。有界超时防止卡住或正在过图的客户端把
HTTP 请求永久挂起；超时时调用以说明原因的消息失败（"The client may be loading, zoned, or busy"），
而不是阻塞。

## 架构

一次工具调用的路径：

```
MCP 客户端
  -> POST http://127.0.0.1:18777/mcp        （HTTP 上的 JSON-RPC 2.0）
  -> MiniHttp         在 TcpListener 上手工解析 HTTP/1.1；无 HTTP.SYS，无 ASP.NET
  -> McpServer        路由、强制认证与会话、分发 JSON-RPC 方法
  -> GameThread       IFramework.RunOnFrameworkThread + 超时
  -> 工具处理器        五个 Tool* 类之一，以 JSON 参数调用
  -> GameServices     Dalamud 服务（IObjectTable、IPartyList、IPlayerState、IDataManager 等）
     + FFXIVClientStructs   直接读取客户端自身结构体
  -> JSON 结果        以 MCP 文本内容序列化返回
```

`MiniHttp` 与 `McpServer` 没有 Dalamud 依赖，这正是冒烟测试能在游戏外编译并驱动它们的原因。
`GameThread` 之上是传输与协议；之下都需要存活客户端。

注册是显式的而非反射的。`Plugin` 构建对象图并对共享 `ToolRegistry` 调用 `ClientTools.Register`、
`ObjectTools.Register`、`DataTools.Register`、`MemoryTools.Register` 和 `StructTools.Register`；注册表的
`AllowMutating` 委托读取 `Configuration.AllowMutatingTools`，所以闸门按调用求值而非启动时缓存。

### 命令

| 命令 | 效果 |
| --- | --- |
| `/dalamudmcp` | 打开设置窗口。 |
| `/dalamudmcp start` / `stop` / `restart` | 控制监听器。 |
| `/dalamudmcp status` | 报告运行状态与端点。 |
| `/dalamudmcp port <1024-65535>` | 更改端口，运行中则重启。 |
| `/dalamudmcp tools` | 打印全部已注册工具名。 |

## 局限

**游戏内验证已完成 —— 一次，在这台机器上，用实时客户端。** 全部 33 个只读工具都在 FFXIV 运行且角色登录的
状态下通过真实端点（`http://127.0.0.1:18777/mcp`）调用过，每一个都返回了真实游戏数据（或对真正为空的
状态返回了正确的"空"答案 —— 单人小队、无 FATE、无目标、空闲量表）。亮点：`get_local_player` 返回了
实时角色（名称、100 级、职业、HP/MP、世界、位置）；`get_game_objects` 枚举了玩家、一只宠物和训练木桩
及距离；`get_job_gauge` 实时解码了 `BardGauge`；`get_status_effects` 在玩家与木桩上都于恰好经审计的 +9136
偏移处定位到 `StatusManager`；`read_object_memory` 在 +144 处返回了预期的 `ObjectKind` 字节；每个 Excel
工具都从客户端自己的数据作答（国服 —— 火之碎晶、吟游诗人、强化药）；`read_memory` 在模块基址返回了
`MZ` 头；`scan_signature` 找到了真实匹配；`get_module_info` 报告了真实的客户端模块。

那次实机会话还抓到了**三个离线套件看不见的真实缺陷**，每个都通过重建 DLL 并让 Dalamud 的开发插件
自动重载接管而在游戏内修复并复验：

1. `get_job_gauge` 在 `Enum.GetName` 内抛出 `ArgumentException`，因为传给反射的是 JSON 侧的 `JValue`
   装箱，而那里需要枚举底层类型的真实 CLR 装箱。现在由独立的 `BoxClr` 帮助函数为反射构建真实装箱
   值，`Box` 继续为 JSON 产生 `JValue`。
2. `get_game_data_sheet_info` 崩溃于 `Could not determine JSON object type for type
   System.Reflection.RuntimePropertyInfo` —— `textColumns` 列表序列化了原始 `PropertyInfo` 对象。现在输出
   属性**名**。
3. `get_game_data_row` **总是读第 0 行**：传给 `TryGetRow` 的反射缓冲从未装箱请求的行 id，且在 .NET 10
   上 `MethodInfo.Invoke` 把 `uint` 参数的 `null` 实参静默转换为 `0` 而不是抛异常 —— 这是测得的行为，
   不是假设。请求第 2 行会返回第 0 行的数据。现在 id 被显式装箱。

那次会话之前已验证、至今仍然成立的内容：

- 插件项目对着真实安装的程序集以 x64 干净构建（`0 errors, 0 warnings`）。
- `tests\ProtocolSmokeTest` 对真实传输与协议源码通过 `47/47`。
- `tests\BridgeSmokeTest` 对构建出的 stdio 桥与真实服务器通过 `22/22`。
- `tests\LoadabilityCheck` 对构建出的插件 DLL 与清单通过 `103/103`，其中包括 Dalamud **自己的**
  `ServiceContainer` 给出的结论：它离线重建容器（`RegisterInterfaces` 是纯特性反射），为每个非 scoped
  服务类型安装一个单例键，并调用 Dalamud 的私有 `FindApplicableCtor`。由四个对照保护 —— 其中一个扣住
  单例键并断言同一构造函数随后被拒绝 —— 所以结论是关于 Dalamud 的，而不是关于测试装置的。偏移审计
  从已安装的库本身重新推导每个手工抄录的 FFXIVClientStructs 偏移。
- `tests\McpInterop` 以**官方** MCP SDK 为客户端通过 `12/12` 协议检查、`2/2` 阴性对照检查与 `11/11`
  真实 schema 检查 —— 组帧由别人的实现裁决，全部 36 个交付 schema 由 SDK 自带的 `ajv` 对照 JSON Schema
  2020-12 元 schema 编译。
- `tests\PluginLoadTest` 在游戏外运行交付的 `Plugin` 构造函数通过 `93/93`：真实加载路径执行、注册全部
  36 个工具、绑定配置端口、在真实 socket 上服务 MCP、校验每个交付工具 schema 并与处理器实际索求交叉
  核对、端到端遵守请求日志与 bearer token 设置、对存活监听器运行每个 `/dalamudmcp` 子命令、通过由本机
  已安装 `sqpack` 文件支撑的 Reflection.Emit 构建的数据管理器读取**真实游戏数据**（物品 1 解析为金币）、
  在自己进程内读取并验证**真实内存**，以及干净卸载。
- 插件 DLL 与清单生成于构建输出，项目引用了正确的 Dalamud 与 FFXIVClientStructs 程序集。

未验证、因此不声称的：上述单次验证会话之外的游戏内行为（一台机器、一个客户端构建、一个登录角色在
一个区域 —— 其他语言环境、吟游诗人之外其他职业的量表、进行中的战斗、小队、副本与 GPose 均未演练）；
偏移与**未来**游戏客户端构建的匹配（它们匹配今天运行的这个客户端）；以及长会话行为，如会话存储的
内存增长或监听器在数小时轮询下的存续。

**其他局限：**

- `tests\LoadabilityCheck` 与 `tests\PluginLoadTest` 从自身二进制位置向上查找仓库根（也可用 `DALAMUD_MCP_REPO`
  环境变量显式指定），并从 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev` 读取 Dalamud 程序集，所以只在本机
  原样运行。使用国际服启动器会以非零码退出。
- 请求日志记录方法、工具名、结果与耗时 —— 刻意不记录参数体或结果载荷，所以它不会告诉你*传了什么*。
  原始内存读取只按名称记录，其参数不会出现在任何地方。
- 插件本身没有 stdio 传输；支持 HTTP 的客户端直连，仅 stdio 的客户端走 `bridge\DalamudMcpBridge`。
- 没有可变更工具，所以 `AllowMutatingTools` 目前对暴露内容没有影响。
- `get_game_objects` 等枚举工具读取整张对象表后在内存中过滤，常规对象数没问题，但不适合高频轮询循环。
- 插件未打包进插件仓库；未引用 `DalamudPackager`。
- 会话存储把会话保存在内存中，2 小时空闲即过期，长期空闲的客户端必须重新 `initialize`。
