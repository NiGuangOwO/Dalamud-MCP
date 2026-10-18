# Native checkbox state

`set_addon_checkbox` changes a native CheckBox through the installed SDK's `SetChecked`, then dispatches the control's registered addon `ButtonClick` notification. An addon-level click alone does not necessarily update the component's checked flag. `click_addon_element` therefore refuses CheckBox controls and points callers to this tool.

```json
{"addon":"SelectYesNoTextScroll","nodeId":3,"checked":true,"expectedChecked":false,"dryRun":true}
```

Use `list_addon_elements` to discover the control and read the dialog first. Remove `dryRun` after checking that the operation is within the user's authorization. The tool does not press a dialog's confirmation button. Checkbox consent and the subsequent game action are separate steps; enabling a button is not proof that a purchase or entry succeeded.

`checked`, `expectedChecked` and `dryRun` accept JSON booleans only. String or numeric coercion is refused. `expectedChecked` guards the prior state. When the state already matches, no native mutation or notification is sent, so retries do not toggle the control back. Optional `componentId` scopes duplicate child node IDs.

Only exact CheckBox metadata, matching owner nodes, visible ancestry, enabled controls and one registered addon ButtonClick notification are accepted. ULD arrays, native copies and event chains are bounded; unreadable, ambiguous or oversized layouts are refused. `dryRun` remains under the mutating-tool permission gate. The `checked` result records the state verified immediately after SetChecked, before the addon notification; callers should verify resulting UI state or availability afterward.

Offline checks cover the installed SDK's checked flag, component discriminators and ownership, invalid pointers, bounded node arrays, duplicate IDs and strict booleans. A limited 2026-10-18 live check on SelectYesNoTextScroll confirmed that the native checkbox became checked and the confirmation button became enabled; repeating the same request sent no second notification.

## 中文

普通点击事件可能只通知窗口，而没有更新复选框自身状态。请使用 `set_addon_checkbox` 显式指定目标状态，先通过 `dryRun` 预览，操作后复核界面。该工具不会自动确认对话框，也不会因为工具存在而扩大用户授权范围。
