# String IPC queries / 字符串 IPC 查询

The endpoint registry additionally supports `Func<string, bool>`, `Func<string, bool?>` and `Func<string, string>`.
The argument must be a JSON string, at most 4096 characters; null, objects and implicit conversion are refused.
Nullable boolean results preserve `null` instead of converting an unknown module to `false`.
All invocation retains the existing mutation gate because an arbitrary provider can have side effects.

端点注册新增上述三种字符串查询签名。参数必须是最多 4096 字符的 JSON 字符串，不接受 null、对象或隐式类型转换。
可空布尔返回值保留 null，不将未知模块等同于禁用状态。调用仍受原有可变更权限控制。

Example using the provider's published source contract / 按提供方源码约定查询：

```json
{"pluginName":"DailyRoutines","methodName":"IsModuleEnabled","signature":"Func<string, bool?>"}
```

Then call the registered endpoint / 随后调用已注册端点：

```json
{"pluginName":"DailyRoutines","methodName":"IsModuleEnabled","value":"AutoOutOnALimb"}
```

`value` is directly inside the tool's arguments object. The result contains `invoked` and the provider's result.
Registration does not enable a module or contact its provider.

`value` 直接位于工具参数对象中，结果包含 `invoked` 和提供方返回值。注册端点不会启用模块，也不会调用提供方。

Validation on 2026-10-17: strict argument checks passed offline; the running provider returned `false` for the module above.
No module configuration was changed during that query.

2026-10-17 验证：离线参数检查通过，运行中的提供方对上述模块返回 false。查询未修改模块配置。
