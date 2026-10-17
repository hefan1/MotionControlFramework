# Equipment.Template

新设备项目的目录模板，不是具体设备实现，也不会被 Host 自动引用。新建设备时复制此项目，按设备需要添加流程、硬件映射、页面和配置，再由对应 Host 显式引用。

```text
HardwareConfig/   控制卡清单（MotionCards.json）、轴参数（AxisParameters.json）、逻辑 DI/DO 映射（IoMapping.json）
Communications/   设备专属 PLC 数据点与命令握手、自定义 TCP 设备协议
Flows/Stations/   各工位独立的 C# 流程步；并行工位通过设备定义的请求/完成信号协作
Alarms/           设备报警定义与条件
ManualOperations/ 设备专属手动动作
Parameters/       工艺动作参数默认模板（ProcessParameters.json）与可选产品配方
TraceData/        SN 关联的设备采集字段与生产记录组装规则
Views/            设备操作、调试和参数页面
```

设备项目里的 JSON 文件是同型号设备共用的默认模板，构建后放在 `EquipmentTemplates/`。首次部署时，Runtime 为物理设备建立独立的生效配置，并关联唯一 `MachineId`；后续升级只补入缺少的模板文件，不覆盖现场值。生效配置保存在软件发布目录之外；每份配置带 `schemaVersion`，设备项目按明确版本迁移和校验，Runtime 保存时自动备份旧文件。产品配方与设备级调试参数分开管理。

Runtime 管理整机运行状态和工位流程的启停协调。各工位流程保留自己的当前步骤；停机报警时整机暂停，复位后启动从中断步骤重新进入。共享轴或运动区域的避让逻辑根据具体设备需求设计。

DI/DO 使用逻辑名称，并在 `HardwareConfig/IoMapping.json` 中关联运动卡或远程 IO 的物理点位。PLC 点位按名称、数据类型和通讯地址定义；设备项目中的 PLC 通讯代码处理命令和应答握手。视觉等设备的自定义 TCP 报文也按设备协议单独组织。公共通讯适配器提供可复用的 Modbus、TCP 等基础传输实现。

追溯记录以 SN 关联产品，包含记录 ID、MachineId、工位、起止时间和整体结果。设备检测项放入带版本的 JSON 明细。生产记录先保存到本地，再由 Runtime 交给 MES 适配器上传；设备项目只负责采集和组装记录，不负责拼接工厂报文。
