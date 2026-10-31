# 内存设备（InMemory）

本目录只有一个东西：`InMemoryTaskDevice`。它实现 `ITaskDevice` 端口，把轴位置、DI/DO 和变量都放在内存字典里，**不连接任何控制卡**。

## 为什么存在

1. `tools/MotionControl.TaskEngine.Checks` 用它在毫秒级跑完任务定义、节点执行和整机运行检查。
2. `src/MotionControl.TaskEngine.Wpf/Simulation` 的离线预览窗口（`--task-simulation`、`--task-editing-preview`）用它显示任务步骤。

## 它不是什么

- **不是产品运行时组件**：`TaskEngine/Execution` 和 `TaskEngine/Machine` 只依赖 `Device/` 里的端口，不引用本目录。任务执行器不知道自己连的是内存设备还是真实控制卡。
- **不能替代设备验收**：现场跑的是 `src/Adapters/MotionCards/ZMotion` 连接的真实控制器（当前为 ZSimu 仿真器）。改这里的代码只会改变离线检查结果，不会改变设备行为。
- **不是完整设备模型**：它按"速度算时长"推进位置，没有伺服动力学、限位电路、原点信号和厂商回零模式。

## 改动规则

- 修改本目录只允许为了保持离线检查可用，不能为了"让检查变绿"而伪造到位、绕过资源占用或跳过报警。
- 设备端口 `ITaskDevice` 在 `src/MotionControl.TaskEngine/Device/`。新增真实设备实现放在设备工程里，不放在本目录。
- 需要验证设备行为时，走正式 Host 加 ZSimu 的屏幕流程，不要把这里的检查结果当成设备验收。
