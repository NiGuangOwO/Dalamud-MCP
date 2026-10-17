# Conditional clicks on rotating nodes

`click_addon_when_node_matches` accepts `matchAxis: "rotation"` and inclusive `minRotation` / `maxRotation` in local radians, restricted to [-pi, pi] without wraparound. Do not provide targetNodeId or width bounds in this mode. Other matching modes retain their rotation/reflection checks.

The request observes and dispatches on the framework thread, once, using the existing visibility, enabled-control, optional integer phase guards, lifetime identity, mutation permission, and 600-frame / 20-second bounds. A dispatched input is not proof of a game result.

On 2026-10-17, MiniGameBotanist node 17 was confirmed as a rotating indicator, unlike its stationary health gauge (node 25). A local angle window [-0.02, 0.02] triggered the ordinary button 24 ButtonPress event in phase 3. Three cuts reduced observed health 10 -> 6 -> 2 -> 0; declining the verified double-down prompt credited 50 MGP. These are observations of one target, not a universal winning angle or a stable cross-version node contract.

## 中文

旋转匹配使用节点局部角度（弧度），不使用桌面坐标，不修改节点或计算结果。每次输入后必须检查实际状态；小游戏的目标点会改变，不能将单次成功的角度当作固定答案。
