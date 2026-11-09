# 职责归位：问题关闭矩阵

## 本批已验证关闭

| 问题 | 处理与验证依据 |
| --- | --- |
| 启动、暂停、复位、终止在 WPF 页面直接协调两套执行状态 | 迁移到已有 MachineDevice 桥接项目的具体 TaskMachineOperations；页面四个入口只调用并显示结果；结构回归通过 |
| 页面、顶部及未来其他入口容易重复实现操作顺序 | 共用同一个已绑定会话的协调实例；显式方法，没有反射、事件总线或通用命令管线 |
| 上下文撤销与迟到启动结果缺乏集中处理 | 协调实例永久失效、阶段性复核原持权者与连接；实际软件控制器延迟反馈验证旧启动被拒绝 |
| 许可已建立但任务启动阶段失败可能留下任务推进 | 失败先尝试暂停原任务，再向原 Runtime 交接报警，完整保留多重异常 |
| 多入口并发可能交错命令 | 协调类明确拒绝并发命令；实际控制器延迟复位期间的启动拒绝检查通过 |
| 参考外部项目可能误引入空驱动或未授权源码 | 未复制 Sophon 代码，未新增厂商占位驱动、FakeAxis 或另一个状态机 |
| 受保护 UI 被重构顺带改变 | 四页和 MainWindow 的 XAML / code-behind 共十个文件与原基线相同 |

## 仍未关闭，不能以本批通过代替

- Host 逐卡混合品牌装配：当前工程级后端选择仍需专项实现与配置迁移。
- ZMotion 原厂 SDK 软件调用链：需相应原厂软件控制器环境；双 Virtual 通过不是该项通过。
- 雷赛、固高等未接入品牌：没有生成空适配器宣称兼容。
- App 启动/连接/退出职责整理、任务页面其余会话装载与展示代码：本批没有重写整套生命周期。
- 正式 Host 屏幕操作及实物限位、制动、安全回路：本批未执行。

## 新人阅读入口

- 页面按钮：`src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs`，只找对应的 `*TaskCoreAsync`。
- 设备操作顺序：`src/MotionControl.TaskEngine.MachineDevice/TaskMachineOperations.cs`。
- 工位及动作收尾：`src/MotionControl.TaskEngine/Machine/TaskMachineController.*`。
- 整机控制权及连接身份：`src/MotionControl.Framework.Runtime/Machine/MachineRuntime.*`。
- 原厂 API：`src/Adapters/MotionCards/<厂商>/Adapter`，不从工艺或 UI 直接调用。
- 结构回归：`tools/Check-CommandBoundaries.py`；真实软件控制器回归：`tools/Verify-ControllerApi.ps1`。

## 证据

https://github.com/hefan1/MotionControlFramework/actions/runs/42657477141

此验证对应本分支的迁移后工作树；未合并 master。
