# Equipment HardwareConfig

这里保存设备项目提供的硬件配置默认模板。每台实际设备有自己的生效配置，保存在发布目录之外。

- 每份 JSON 文件根节点带有正整数 `schemaVersion`。
- `MotionCards.json`：本设备使用的控制卡标识、适配器名称和连接配置文件。
- `AxisParameters.json`：逻辑轴所属控制卡、物理轴通道、工程单位换算、软限位、回零及轴级默认运动参数。
- `IoMapping.json`：逻辑 DI/DO 名称、所属控制卡和物理 IO 点位映射，并用 `emergencyStopInputName` 指定急停状态输入。

这三份文件各自保存一类设置。运动卡适配器的连接配置只保存厂商连接参数，不重复保存轴参数或 IO 映射。

Runtime 首次启动时只复制缺失的默认文件，不覆盖现场文件。现场配置升级时先读取 `schemaVersion`，由设备项目按明确版本转换并校验；保存新文件时 Runtime 会先备份旧文件。产品工艺动作参数放在 `Parameters/`，PLC 或其他设备的连接与协议参数由对应 `Communications/` 模块管理。

Host 在创建运动卡适配器和单机 Runtime 前，应把这些配置转换为 `MotionCardHardwareDefinition`、`AxisHardwareDefinition`、`DigitalIoHardwareDefinition`，并调用 `MachineHardwareConfigurationValidator.Validate`。校验会检查卡、轴和 IO 的名称/通道重复、关联缺失、限位范围和急停 DI 映射；急停名称必须对应一个已配置的数字输入。模板中的空配置需要设备项目补齐后才能通过校验。

Runtime 通过 `IMachineSafetyStatus` 周期读取急停逻辑输入。急停有效或状态读取失败时会产生锁存停机报警、请求流程暂停，并禁止复位或启动；输入恢复后仍须人工复位。此软件状态用于监控和流程联锁，不代替硬件急停安全回路。
