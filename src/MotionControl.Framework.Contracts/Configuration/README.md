# Configuration Contracts

当前没有必须跨模块共享的设备参数模型。硬件映射、轴参数、工艺参数由各 Equipment 项目定义具体类型；Runtime 的 `MachineConfigurationStore` 提供按 `MachineId` 隔离的 JSON 文件读写和备份。

首期不设计能装载任意对象或任意配置树的通用配置引擎。参数类型与具体校验保持在设备项目，跨模块只共享已确认的运行契约。
