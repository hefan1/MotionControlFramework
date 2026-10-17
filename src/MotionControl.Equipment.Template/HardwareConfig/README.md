# Equipment HardwareConfig

这里保存设备项目提供的硬件配置默认模板。每台实际设备有自己的生效配置，保存在发布目录之外。

- 每份 JSON 文件根节点带有正整数 `schemaVersion`。
- `MotionCards.json`：本设备使用的控制卡标识、适配器名称和连接配置文件。
- `AxisParameters.json`：逻辑轴所属控制卡、物理轴通道、工程单位换算、软限位、回零及轴级默认运动参数。
- `IoMapping.json`：逻辑 DI/DO 名称、所属控制卡和物理 IO 点位映射。

这三份文件各自保存一类设置。运动卡适配器的连接配置只保存厂商连接参数，不重复保存轴参数或 IO 映射。

Runtime 首次启动时只复制缺失的默认文件，不覆盖现场文件。现场配置升级时先读取 `schemaVersion`，由设备项目按明确版本转换并校验；保存新文件时 Runtime 会先备份旧文件。产品工艺动作参数放在 `Parameters/`，PLC 或其他设备的连接与协议参数由对应 `Communications/` 模块管理。
