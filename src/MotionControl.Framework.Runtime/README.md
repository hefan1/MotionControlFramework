# Framework.Runtime

设备运行时通用能力，不包含具体设备工艺和厂商 API。

按职责分区：Machine（单机生命周期）、FlowScheduling（工位流程调度）、Alarms、ManualControl、Configuration、Diagnostics、Traceability。Runtime 统一持有整机状态并协调各工位的启动、暂停、复位和报警响应；工位流程本身由设备项目以 C# 步骤编写，并各自保留当前步骤。流程执行保持直观可读，Runtime 不承载具体工艺动作。

`Machine/MotionCardGroup` 管理 Host 创建的多张运动卡：校验卡号和逻辑轴名称、按配置顺序连接、失败时回收本次已连接的卡，并按逻辑轴名称提供轴对象。设备流程无需知道控制卡厂商、物理卡号或物理轴通道。

`Machine/MachineRuntime` 提供单机连接、复位、启动、暂停和断开入口。连接后必须先复位再启动；暂停会取消各工位当前步骤并等待其退出，复位后启动时由工位流程从保留步骤重新进入。未处理的工位异常会使整机进入 `Alarmed`，并暂停其他工位。Runtime 使用 `IMachineAlarmService` 接收设备项目上报的报警，并把停机报警转换为整机暂停。

`FlowScheduling/StationFlowScheduler` 并行执行每个工位的 `ExecuteCurrentStepAsync`。工位流程由设备项目编写，步骤完成前保持当前步骤不变；暂停或停机报警会请求各流程退出当前步骤，并等待受控停止完成。

`Alarms/MachineAlarmService` 保存当前报警。设备项目上报报警定义、来源、条件状态和补充说明；整机复位前先确认停机报警条件已恢复，完成轴与工位复位后再清除停机报警记录。非停机报警只在条件成立期间显示，条件恢复后自动移除。报警列表变化和新停机报警分别通过事件通知界面与整机运行时。急停回路和急停状态仍单独接入，不用普通报警或流程取消代替。
