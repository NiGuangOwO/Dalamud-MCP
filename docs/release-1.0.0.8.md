# DalamudMCP 1.0.0.8

新增三项通用工具：`list_plugin_commands` 读取运行中实际注册的命令帮助；`open_plugin_ui` 调用一个精确匹配的已加载插件的公开主界面/设置回调；`get_tribal_quest_state` 读取真实部族任务受理限额、友好度及日常任务账本。

工具按范围、数量和输出长度设限；UI 打开受修改权限闸门控制，不猜测插件专用命令，不将请求分发当成窗口出现。部族名称、等级和位置使用安装客户端的数据表解析，未知额度或等级不伪造为零。更新项目约束、服务端指引和中英文工具文档，入口发现工作流见 [运行时插件发现](runtime-plugin-discovery.md)。

Debug/Release 构建无警告、无错误。NativeUiReadTest 57/57，PluginLoadTest 138/138，ProtocolSmokeTest 50/50。现场核对运行中 Debug DLL 的 MVID，读取 Questionable 注册帮助、打开其主界面，读取 12 次实际限额并完成第一项骨颌族日常，游戏系统消息与账本证明交付完成且友好度增加 70。测试未读取目标插件本地源码。
