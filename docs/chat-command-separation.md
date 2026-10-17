# Chat and command separation

Use `slash_command` for game and plugin commands. `send_chat` prefixes the requested channel, so a slash command in its body would otherwise be broadcast as ordinary text. It now refuses leading-slash text (including leading whitespace) and line breaks/NUL before invoking native chat delivery. It does not silently reroute or execute rejected text.

游戏或插件命令必须使用 `slash_command`。`send_chat` 会添加聊天频道前缀，因此不能用来执行命令；工具现在拒绝以斜杠开头的内容及换行/NUL，避免将测试命令广播到频道。

Offline regression: `dotnet run --project tests/ChatInputTest` loads the built implementation and tests the validation without dispatching input. Live refusal can safely be verified with an example command passed to send_chat; the response must be an error naming slash_command.
