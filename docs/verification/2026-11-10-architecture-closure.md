# 职责归位：问题关闭矩阵

日期：2026-11-10。分支：`codex/architecture-clarity-20261110`。PR：#9。

## 本批范围与实际改变

围绕一条完整的设备操作链整理职责：WPF 页面负责提交操作与呈现结果；现有 MachineDevice 桥接项目中的具体 `TaskMachineOperations` 负责复位、启动、暂停、终止顺序；Runtime 仍负责整机控制权与连接身份；TaskMachineController 仍负责任务工步与动作收尾。

没有新建程序集、通用命令框架、反射注册系统、厂商占位驱动或第二套运动状态机。没有复制 Sophon 源码。四条页面操作路径改为调用协调对象；原页面文件净减少 56 行，但新增了独立协调实现与回归代码，**不能把本次描述成全仓库总代码量下降或整套架构已全部重构完成**。

## 本批问题关闭情况

| 编号 | 问题 | 实现与验证依据 |
| --- | --- | --- |
| A01 | 启动、暂停、复位、终止在 WPF 页面直接协调两套执行状态 | 已迁移到具体 TaskMachineOperations；三个结构回归检查原源码失败、迁移后通过；实际软件控制器操作回归通过 |
| A02 | 页面、顶部及后续入口容易重复实现设备操作顺序 | 所有正式页面操作委托同一已绑定会话的协调实例；没有新增泛型管线或事件转发层 |
| A03 | 上下文撤销与迟到启动结果处理分散 | 协调实例永久撤销、阶段性复核原设备持权及连接；软件控制器延迟反馈下撤销启动，迟到结果被拒绝，两个执行层均不继续 Running |
| A04 | 启动失败收尾没有区分尚未启动与可能部分启动 | 在调用任务 Start 前记录是否已经尝试启动；未启动时不误调 Pause，可能部分启动时仍尝试暂停并向原 Runtime 交接故障，保留全部异常 |
| A05 | 同一设备重复获取协调对象可能产生多套命令锁 | 设备只创建一个协调对象；并发调用先核对原持权，再在设备锁内返回同一实例；实际 API 回归先观察重复对象失败，再验证修复通过 |
| A06 | 重新获取操作入口可能复活已撤销的会话 | 缓存实例不因撤销被替换；再次获取得到相同已失效实例，启动明确拒绝。软件控制器用例通过 |
| A07 | 测试可能把任意 InvalidOperationException 当成预期拒绝 | 对并发、旧会话、撤销场景分别检查具体错误原因；不再用不相关状态错误冒充安全检查生效 |
| A08 | 受保护 UI 被重构顺带改变 | 轴映射、IO 映射、轴手动、IO 监控及 MainWindow 的 XAML 与 code-behind 共十个文件保持原基线；仍不代表真实屏幕走查已执行 |

## 验证记录与边界

1. 迁移后的候选工作树在运行 **42657477141** 完成：结构红灯/绿灯、十个受保护文件比较、正式 Host 编译、15/15 内部检查、单/双软件控制器与新增操作协调 API 回归。验证后才提交生产代码。
2. 自审追加“同会话唯一入口”回归后，运行 **42658517177** 在真实软件控制器 API 步骤按预期失败，具体原因为“同一任务设备重复获取操作入口时创建了第二套命令门禁”，没有跳过或放宽断言。
3. 修复提交 **eeef0969b68afadffb82f47620133f44ed3620be** 的运行 **42659231743** 已成功，覆盖共享命令锁、准确拒绝原因、撤销不可复活与原有单/双软件控制器路径。
4. 临时迁移脚本已经移除，临时工作流随交付清理；最终分支仍须由常规 `windows-checks.yml` 完整检查。最终提交与最终运行链接记录在 PR #9，不能拿前一版本通过替代最新版本通过。

证据：
- https://github.com/hefan1/MotionControlFramework/actions/runs/42657477141
- https://github.com/hefan1/MotionControlFramework/actions/runs/42658517177
- https://github.com/hefan1/MotionControlFramework/actions/runs/42659231743
- https://github.com/hefan1/MotionControlFramework/pull/9

本轮由主代理静态复核及自动化验证，未声称已有独立第三方代码审查。

## 尚未关闭，不能以本批通过代替

| 事项 | 当前状态 | 关闭所需证据 |
| --- | --- | --- |
| Host 逐卡混合品牌装配 | 目前仍按工程选择后端 | 显式逐卡创建、配置迁移、混合后端及失败回滚验证 |
| ZMotion 原厂 SDK 软件调用链 | 本轮未执行 | ZMotionCard 经对应原厂 SDK 连接兼容软件控制器并实际回读；无需将实体卡作为本阶段前提 |
| 雷赛、固高等其他品牌 | 尚未接入完整适配器 | 实际厂商 API 和能力准入用例；不能靠空实现宣称支持 |
| App 启动、连接、退出职责整理 | 本批未重写 | 生命周期调用边界与对应断连、退出、恢复回归 |
| TaskMonitorLauncher 剩余装载与展示职责 | 本批只迁移四条操作路径 | 单独整理会话发布、显示投影等职责并维持设备身份隔离 |
| 正式 Host 屏幕走查及实体安全 | 本轮未执行 | 正式窗口操作证据；实际硬限位、机械制动和安全回路另行现场验收 |

## 新人阅读入口

- 页面按钮：`src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs`，对应 `*TaskCoreAsync` 只做操作调用和显示结果。
- 设备操作顺序与会话命令锁：`src/MotionControl.TaskEngine.MachineDevice/TaskMachineOperations.cs`。
- 工位与动作收尾：`src/MotionControl.TaskEngine/Machine/TaskMachineController.*`。
- 整机控制权、报警与连接身份：`src/MotionControl.Framework.Runtime/Machine/MachineRuntime.*`。
- 原厂 API：`src/Adapters/MotionCards/<厂商>/Adapter`，不从工艺或 UI 直接调用。
- 结构回归：`tools/Check-CommandBoundaries.py`。
- 正式软件控制器回归：`tools/Verify-ControllerApi.ps1`；新增场景位于 `Program.Commands.cs`。

未合并 master；此表只关闭有明确实现和验证证据的本批问题，不表示所有历史问题、新品牌或整机安全已经验收。
