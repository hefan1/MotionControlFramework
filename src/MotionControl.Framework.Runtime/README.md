# Framework.Runtime

设备运行时通用能力，不包含具体设备工艺和厂商 API。

按职责分区：Machine（单机生命周期）、FlowScheduling（工位流程调度）、Alarms、ManualControl、Configuration、Diagnostics、Traceability。Runtime 统一持有整机状态并协调各工位的启动、暂停、复位和报警响应；工位流程本身由设备项目以 C# 步骤编写，并各自保留当前步骤。流程执行保持直观可读，Runtime 不承载具体工艺动作。

`Machine/MotionCardGroup` 管理 Host 创建的多张运动卡：校验卡号和逻辑轴名称、按配置顺序连接、失败时回收本次已连接的卡，并按逻辑轴名称提供轴对象。设备流程无需知道控制卡厂商、物理卡号或物理轴通道。
