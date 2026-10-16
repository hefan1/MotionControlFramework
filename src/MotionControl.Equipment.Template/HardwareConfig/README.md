# Equipment HardwareConfig

这里保存设备项目提供的硬件配置默认模板。每台实际设备有自己的生效配置，保存在发布目录之外。

- `AxisParameters.json`：控制器和轴通道、工程单位换算、软限位、回零及轴级默认运动参数。
- `IoMapping.json`：逻辑 DI/DO 名称到运动卡或远程 IO 物理点位的映射。

现场配置升级时先备份，再根据配置版本迁移和验证。产品工艺动作参数放在 `Parameters/`，PLC 或其他设备的连接与协议参数由对应 `Communications/` 模块管理。
