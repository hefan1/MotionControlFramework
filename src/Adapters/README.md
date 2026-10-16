# Adapters

适配器按可替换边界分别创建项目；框架运行时不依赖任何适配器实现。

- `MotionCards/`：按厂商拆分的原生 API 项目，以及后续实现通用运动卡契约的驱动适配器。原生 API 和设备业务流程彼此隔离。
- `Communications/`：可复用的 Modbus、TCP 等基础传输。设备专属 PLC 点位、握手和视觉报文定义放在对应 Equipment 项目。
- `Storage/`：本地生产记录和持久化待上传队列实现。
- `Mes/`：按工厂或项目拆分的 MES 上传协议与字段映射。
