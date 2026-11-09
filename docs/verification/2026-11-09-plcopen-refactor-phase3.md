# PLCopen 架构重构第三批：移除任务控制器对内存设备类型的硬编码

日期：2026-11-09  
上游：`codex/plcopen-refactor-phase2-20261109`，基线提交 `2edb4f34d8337e1d7cbb98d328fbc1e0e4c81b76`  
工作分支：`codex/plcopen-refactor-phase3-20261109`

## 前置验收

第二批 Windows CI [42610317757](https://github.com/hefan1/MotionControlFramework/actions/runs/42610317757) **已成功**：正式 Host 构建通过、15/15 个内部检查项目通过、受保护 UI 基线通过；现有独立软件控制器在正式 `VirtualMotionCard` / `IAxis` API 路径完成回零、绝对/相对定位、点动、Abort 拒绝、Controlled 停止及停稳反馈验证。该结果不代表厂商 SDK、实际机器或 PLCopen 一致性认证。

## R05：解除正式任务控制器与测试实现具体类型的耦合

### 源码问题

`src/MotionControl.TaskEngine/Machine/TaskMachineController.Control.cs` 原有三处判断 `_device is InMemoryTaskDevice`：

1. `ValidateResetConditions`：仅在非内存设备上检查需定位轴的伺服及回零状态。
2. `Pause`：仅内存设备允许同步修改暂停状态；其他设备必须使用 `PauseAsync` 并等待停止收尾。
3. `SetStopAlarm`：仅内存设备允许同步设置停机报警；其他设备必须使用 `SetStopAlarmAsync`。

这里的安全差异有明确实际用途，却直接引用了一个测试实现类型。未来更换任务设备实现时，控制器不应靠类名或类型转换来判断是否具备停止收尾能力。

### 本轮具体修改

- **新增 `Device/ITaskDeviceExecutionPolicy.cs`（internal）**：只为框架内部可知的纯内存任务设备声明两个独立规则：是否要求在复位时检查物理轴就绪，以及是否允许没有物理收尾的同步状态改变。未实现接口的所有设备默认严格：**必须做轴就绪检查、禁止同步停机绕过收尾**。
- **`Device/InMemory/InMemoryTaskDevice.cs`**：利用已有内存设备实例显式声明原行为（复位不用物理轴就绪、允许仅修改内存状态的同步暂停/停机报警），不建立新虚拟轴或模拟控制器，也不增加生产运动 API。
- **`Machine/TaskMachineController.cs`**：控制器创建时读取并冻结执行规则，后续不在不同阶段反复检查设备实现类型。
- **`Machine/TaskMachineController.Control.cs`**：三处具体类判断改为清晰的只读执行规则条件，既有安全异常说明和异步命令路径不变；移除对 `Device.InMemory` 命名空间的引用。
- **对外兼容**：公开 `ITaskDevice` 接口和 `TaskMachineController` 构造参数均未变化；正式 `MachineTaskDevice` 无需引入测试接口，它不实现内部规则时自动使用严格模式。

**安全界限**：只允许框架内部已知的、不向物理设备发运动/输出命令的实现明确启用宽松同步路径。它不是 PLCopen 的功能块接口，不替代 `MC_Stop`、`MC_Halt`、硬件急停和安全回路。厂商适配器依旧必须提供可信停止/回零反馈；缺失能力时不伪造状态。

## R06：正式任务装载禁止隐式回退内存设备

- **原问题**：`TaskMonitorSession.LoadMachine(store, machineId)` 调用可空设备版本，后者执行 `device ?? new InMemoryTaskDevice(catalog)`。正式 Host 当前已正确传入设备，因此未发现现有 Host 直接误用；但新接入的代码若漏传设备，就可能创建不接真实控制器的任务执行器，造成“任务看似加载、实际没有调用硬件”的维护风险。
- **修复**：移除不要求设备的双参数重载。四参数正式入口现强制要求 `device` 和 `alarmService` 非空，并在读取任务工程文件前拒绝缺失参数；不再自动创建内存设备。正式 `HostTaskSessionLoader.Load` 本来就传入当前 `MachineTaskDevice` 和 `runtime.AlarmService`，调用方式保持不变。
- **兼容性**：此项是有意的源码接口收紧。旧代码若调用双参数 `LoadMachine`，需要改为显式取得真实设备和报警服务后调用四参数入口。若只需要展示独立离线示例，应明确使用 `TaskMonitorExample.Create()`，不能伪装成设备实际运行会话。
- **新增测试**：在已有 `MotionControl.TaskMonitor.Checks` 增加“正式装载缺少设备时禁止内存回退”一组，直接断言缺少设备立即拒绝，不启动控制器、不创建模拟轴。
- **PLCopen 语义**：此项并非 PLCopen 单轴功能块的具体实现，而是保障规范化运动命令真正进入已指定的设备 API，避免把不具硬件反馈的离线执行当作正式设备。

## 测试与保护范围

- 本批未修改 `AxisMappingConfigurationView.xaml`、`IoMappingConfigurationView.xaml`、`ManualAxisControl.xaml`、`IoMonitorView.xaml` 和 `MainWindow.xaml`，也不修改上述四页后台代码。
- 本批没有新增 `FakeAxis`、`InMemoryAxis` 或其他模拟运动模型；上一批新增的正式软件控制器 API 验证继续在 CI 中执行。
- 原有离线内存任务测试保留用于流程调度、状态与资源异常分支，不作为运动能力或真实设备验收。
- 本轮仍须等待当前分支 Windows CI 的实际结果，完成之前不得标注全部通过或合并主分支。
- 如未来有新厂商或新后端，未实现内部策略的默认是严格安全路径，而不是自动允许省略停稳检查。

## 后续架构工作候选（未在本批实现）

1. 继续检查 `MachineRuntime` 与 `TaskMachineController` 的控制权、状态和异常交接，只有证实重复才合并，避免再新增通用管理器。
2. 对照 PLCopen Part 1，在不同厂商可读的轴命令状态、动作结果和停止反馈有真实来源后，再新增必要的契约字段；禁止从 `Moving` 单独推算完整 PLCopen 轴状态机。
3. 针对四个受保护页面，在正式 Host 的设备切换、断连、重连、旧异步回调场景下继续做屏幕回归；布局保护脚本不能代替运行验收。
